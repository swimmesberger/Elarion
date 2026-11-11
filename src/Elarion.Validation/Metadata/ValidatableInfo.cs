#if NET11_0_OR_GREATER
// Adapted from the Microsoft.Extensions.Validation 11.0.0 source-generator templates (dotnet/aspnetcore,
// src/Validation), Copyright (c) .NET Foundation and Contributors, licensed under the MIT License — see
// THIRD-PARTY-NOTICES.md. Microsoft.Extensions.Validation 11 no longer exposes its walker base classes; its own
// generator compiles this walker into each application instead, so Elarion carries the same walker here for its
// generated module resolvers (ADR-0027). Keep behavioral changes out of this copy: re-sync it from the templates
// of each new Microsoft.Extensions.Validation release instead.

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Validation;

namespace Elarion.Validation.Metadata;

/// <summary>
/// Shared attribute-evaluation logic of the validation walker behind <see cref="ValidatableTypeInfo"/> and
/// <see cref="ValidatablePropertyInfo"/>. Infrastructure for source-generated resolvers; not for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ValidatableInfo {
    private protected ValidatableInfo() { }

    private protected abstract void ReportError(
        ValidateContext context,
        string displayName,
        object? container,
        ValidationAttribute attribute,
        ValidationResult result);

    private protected static bool IsEnumerable(Type type) {
        if (type.IsGenericType) {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>)
                || definition == typeof(List<>) || definition == typeof(IList<>))
                return true;
        }

        if (type.IsArray) return true;

        return typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string);
    }

    private protected static bool TryGetRequiredAttribute(ValidationAttribute[] attributes,
        out RequiredAttribute? requiredAttribute) {
        foreach (var attribute in attributes) {
            if (attribute is RequiredAttribute required) {
                requiredAttribute = required;
                return true;
            }
        }

        requiredAttribute = null;
        return false;
    }

    private protected static string? ResolveAttributeErrorMessage(
        ValidateContext context,
        string? memberName,
        string displayName,
        Type declaringType,
        ValidationAttribute attribute,
        ValidationResult result) {
        if (attribute.ErrorMessageResourceType is not null) return result.ErrorMessage;

        if (context.ServiceProvider?.GetService(typeof(IStringLocalizerFactory)) is not IStringLocalizerFactory
            localizerFactory)
            return result.ErrorMessage;

        var localizer = LocalizationHelpers.CreateStringLocalizer(context, declaringType, localizerFactory);
        var localizedTemplate =
            LocalizationHelpers.FindLocalizedTemplate(localizer, attribute, memberName, declaringType);

        return localizedTemplate is null ? result.ErrorMessage : attribute.FormatMessage(localizedTemplate, displayName);
    }

    private protected async Task ValidateAttributesAsync(
        ValidateContext context,
        ValidationAttribute[] validationAttributes,
        object? value,
        object? container,
        ValidationContext validationContext,
        string displayName,
        CancellationToken cancellationToken) {
        // Asynchronous attributes run only once every synchronous attribute has passed.
        if (ValidateSynchronousOnly(context, validationAttributes, value, container, validationContext, displayName))
            await ValidateAsynchronousOnlyAsync(context, validationAttributes, value, container, validationContext,
                displayName, cancellationToken).ConfigureAwait(false);
    }

    private protected void ValidateAllAttributesSynchronously(
        ValidateContext context,
        ValidationAttribute[] validationAttributes,
        object? value,
        object? container,
        ValidationContext validationContext,
        string displayName) {
        foreach (var attribute in validationAttributes) {
            var result = attribute.GetValidationResult(value, validationContext);
            if (result is not null && result != ValidationResult.Success)
                ReportError(context, displayName, container, attribute, result);
        }
    }

    private bool ValidateSynchronousOnly(
        ValidateContext context,
        ValidationAttribute[] validationAttributes,
        object? value,
        object? container,
        ValidationContext validationContext,
        string displayName) {
        var hasErrors = false;
        foreach (var attribute in validationAttributes) {
            if (attribute is AsyncValidationAttribute) continue;

            var result = attribute.GetValidationResult(value, validationContext);
            if (result is null || result == ValidationResult.Success) continue;

            hasErrors = true;
            ReportError(context, displayName, container, attribute, result);
        }

        return !hasErrors;
    }

    private async Task ValidateAsynchronousOnlyAsync(
        ValidateContext context,
        ValidationAttribute[] validationAttributes,
        object? value,
        object? container,
        ValidationContext validationContext,
        string displayName,
        CancellationToken cancellationToken) {
        CancellationTokenSource? linkedCts = null;
        try {
            var tracker = new AsyncValidationTracker(context);
            foreach (var attribute in validationAttributes) {
                if (attribute is not AsyncValidationAttribute asyncAttribute) continue;

                linkedCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                tracker.Track(GetValidationResultCoreAsync(asyncAttribute, value, container, tracker.NextContext(),
                    validationContext, displayName, cancellationToken, linkedCts));
            }

            await tracker.CompleteAsync().ConfigureAwait(false);
        }
        finally {
            linkedCts?.Dispose();
        }
    }

    private async Task GetValidationResultCoreAsync(
        AsyncValidationAttribute attribute,
        object? value,
        object? container,
        ValidateContext context,
        ValidationContext validationContext,
        string displayName,
        CancellationToken originalCancellationToken,
        CancellationTokenSource linkedCancellationTokenSource) {
        // The linked source combines the caller's token with a short-circuit on the first failing attribute.
        try {
            var result = await attribute
                .GetValidationResultAsync(value, validationContext, linkedCancellationTokenSource.Token)
                .ConfigureAwait(false);
            if (result is not null && result != ValidationResult.Success) {
                ReportError(context, displayName, container, attribute, result);
                linkedCancellationTokenSource.Cancel();
            }
        }
        catch (OperationCanceledException) when (linkedCancellationTokenSource.IsCancellationRequested
                                                 && !originalCancellationToken.IsCancellationRequested) {
            // Cancelled by the short-circuit above, not by the caller: the failing attribute already reported.
        }
    }

    /// <summary>
    /// Runs sibling validations concurrently without sharing a <see cref="ValidateContext"/> between two in-flight
    /// validations: the context is reused while validations complete synchronously and cloned only after one goes
    /// asynchronous; errors of the clones are merged back into the original context on completion.
    /// </summary>
    private protected struct AsyncValidationTracker {
        private readonly ValidateContext _originalContext;
        private readonly int _originalDepth;
        private readonly string _originalPath;

        private bool _nextNeedsClone;
        private ValidateContext _currentContext;
        private List<ValidateContext>? _clonedContexts;
        private List<Task>? _pendingTasks;

        public AsyncValidationTracker(ValidateContext context) {
            _originalContext = context;
            _currentContext = context;
            _originalDepth = context.CurrentDepth;
            _originalPath = context.CurrentValidationPath;
        }

        public ValidateContext NextContext() {
            if (!_nextNeedsClone) return _currentContext;

            _currentContext = new ValidateContext {
                ValidationOptions = _originalContext.ValidationOptions,
                ServiceProvider = _originalContext.ServiceProvider,
                CurrentDepth = _originalDepth,
                CurrentValidationPath = _originalPath
            };
            (_clonedContexts ??= []).Add(_currentContext);
            _nextNeedsClone = false;

            return _currentContext;
        }

        public void Track(Task validationTask) {
            if (validationTask.IsCompletedSuccessfully) return;

            _nextNeedsClone = true;
            (_pendingTasks ??= []).Add(validationTask);
        }

        /// <summary>
        /// Completes synchronously when nothing went asynchronous; otherwise awaits every pending validation and
        /// reports whether a cloned context recorded an error.
        /// </summary>
        public readonly Task<bool> CompleteAsync() {
            return _pendingTasks is null
                ? Task.FromResult(false)
                : AwaitAndMergeAsync(_pendingTasks, _clonedContexts, _originalContext);
        }

        private static async Task<bool> AwaitAndMergeAsync(List<Task> pendingTasks,
            List<ValidateContext>? clonedContexts, ValidateContext originalContext) {
            await Task.WhenAll(pendingTasks).ConfigureAwait(false);

            if (clonedContexts is null) return false;

            var hasErrors = false;
            foreach (var clonedContext in clonedContexts) {
                if (clonedContext.ValidationErrors is null) continue;

                foreach (var (_, errors) in clonedContext.ValidationErrors) {
                    hasErrors = true;
                    foreach (var error in errors) originalContext.AddValidationError(error);
                }
            }

            return hasErrors;
        }
    }
}
#endif
