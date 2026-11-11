#if NET11_0_OR_GREATER
// Adapted from the Microsoft.Extensions.Validation 11.0.0 source-generator templates (dotnet/aspnetcore,
// src/Validation), Copyright (c) .NET Foundation and Contributors, licensed under the MIT License — see
// THIRD-PARTY-NOTICES.md. Difference from the template: the type's display name is its CLR name (as in
// Microsoft.Extensions.Validation 10) instead of a localizable DisplayNameInfo.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Validation;

namespace Elarion.Validation.Metadata;

/// <summary>
/// Validation metadata for a validatable type: validates its members (including members inherited from validatable
/// base types and interfaces), then — only when no member failed — its type-level attributes, then — only when those
/// passed — <see cref="IValidatableObject"/>/<see cref="IAsyncValidatableObject"/>. Infrastructure for
/// source-generated resolvers; not for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ValidatableTypeInfo : ValidatableInfo, IValidatableTypeInfo {
    private static readonly object ThrowawayObjectInstance = new();

    private readonly Type[] _implementedInterfaces;

    /// <summary>Creates the metadata for <paramref name="type"/> and its validatable <paramref name="members"/>.</summary>
    /// <param name="type">The validatable type; its interfaces are inspected for inherited validatable members.</param>
    /// <param name="members">The type's own validatable properties.</param>
    protected ValidatableTypeInfo(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
        Type type,
        IReadOnlyList<ValidatablePropertyInfo> members) {
        Type = type;
        Members = members;
        _implementedInterfaces = type.GetInterfaces();
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    internal Type Type { get; }

    internal IReadOnlyList<ValidatablePropertyInfo> Members { get; }

    /// <summary>Returns the validation attributes declared on the type itself.</summary>
    protected abstract ValidationAttribute[] GetValidationAttributes();

    /// <inheritdoc />
    public bool TryFindProperty(string propertyName, ValidationOptions validationOptions,
        [NotNullWhen(true)] out IValidatablePropertyInfo? validatablePropertyInfo) {
        if (FindLocalMember(propertyName) is { } localMember) {
            validatablePropertyInfo = localMember;
            return true;
        }

        foreach (var @interface in _implementedInterfaces) {
            if (validationOptions.TryGetValidatableTypeInfo(@interface, out var interfaceTypeInfo)
                && interfaceTypeInfo.TryFindProperty(propertyName, validationOptions, out validatablePropertyInfo))
                return true;
        }

        var baseType = Type.BaseType;
        while (baseType is not null) {
            if (validationOptions.TryGetValidatableTypeInfo(baseType, out var baseTypeInfo))
                return baseTypeInfo.TryFindProperty(propertyName, validationOptions, out validatablePropertyInfo);

            baseType = baseType.BaseType;
        }

        validatablePropertyInfo = null;
        return false;
    }

    /// <inheritdoc />
    public virtual async Task ValidateAsync(object? value, ValidateContext context,
        CancellationToken cancellationToken) {
        var displayName = Type.Name;
        if (value is null) {
            // Without an instance only the type-level attributes can run: no members, no IValidatableObject.
            await ValidateAttributesAsync(context, GetValidationAttributes(), value, value,
                new ValidationContext(ThrowawayObjectInstance, displayName, context.ServiceProvider, null),
                displayName, cancellationToken).ConfigureAwait(false);
            return;
        }

        ValidateDepth(context);
        var originalErrorCount = context.ValidationErrors?.Count ?? 0;

        var tracker = new AsyncValidationTracker(context);
        tracker = ValidateMembers(value, tracker, cancellationToken);
        foreach (var superTypeInfo in GetSuperTypeInfos(value.GetType(), context.ValidationOptions))
            tracker = superTypeInfo.ValidateMembers(value, tracker, cancellationToken);

        var clonedContextsHaveErrors = await tracker.CompleteAsync().ConfigureAwait(false);
        if ((context.ValidationErrors?.Count ?? 0) > originalErrorCount || clonedContextsHaveErrors) return;

        var validationContext = new ValidationContext(value, displayName, context.ServiceProvider, null);
        await ValidateAttributesAsync(context, GetValidationAttributes(), value, value, validationContext,
            displayName, cancellationToken).ConfigureAwait(false);
        if ((context.ValidationErrors?.Count ?? 0) > originalErrorCount) return;

        if (typeof(IValidatableObject).IsAssignableFrom(Type))
            await ValidateValidatableObjectAsync(value, context, validationContext, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual void Validate(object? value, ValidateContext context) {
        var displayName = Type.Name;
        if (value is null) {
            ValidateAllAttributesSynchronously(context, GetValidationAttributes(), value, value,
                new ValidationContext(ThrowawayObjectInstance, displayName, context.ServiceProvider, null),
                displayName);
            return;
        }

        ValidateDepth(context);
        var originalErrorCount = context.ValidationErrors?.Count ?? 0;

        ValidateMembersSynchronously(value, context);
        foreach (var superTypeInfo in GetSuperTypeInfos(value.GetType(), context.ValidationOptions))
            superTypeInfo.ValidateMembersSynchronously(value, context);

        if ((context.ValidationErrors?.Count ?? 0) > originalErrorCount) return;

        var validationContext = new ValidationContext(value, displayName, context.ServiceProvider, null);
        ValidateAllAttributesSynchronously(context, GetValidationAttributes(), value, value, validationContext,
            displayName);
        if ((context.ValidationErrors?.Count ?? 0) > originalErrorCount) return;

        // Like the template: IValidatableObject runs when the metadata's own type implements it, not a subtype.
        if (!typeof(IValidatableObject).IsAssignableFrom(Type) || value is not IValidatableObject validatable) return;

        var errorPrefix = context.CurrentValidationPath;
        foreach (var validationResult in validatable.Validate(validationContext))
            ReportValidatableObjectResult(validationResult, errorPrefix, value, context);
    }

    private ValidatablePropertyInfo? FindLocalMember(string memberName) {
        foreach (var member in Members)
            if (string.Equals(member.Name, memberName, StringComparison.Ordinal))
                return member;

        return null;
    }

    private void ValidateDepth(ValidateContext context) {
        if (context.CurrentDepth >= context.ValidationOptions.MaxDepth)
            throw new InvalidOperationException(
                $"Maximum validation depth of {context.ValidationOptions.MaxDepth} exceeded at "
                + $"'{context.CurrentValidationPath}' in '{Type.Name}'. This is likely caused by a circular reference "
                + "in the object graph. Consider increasing the MaxDepth in ValidationOptions if deeper validation is "
                + "required.");
    }

    private AsyncValidationTracker ValidateMembers(object value, AsyncValidationTracker tracker,
        CancellationToken cancellationToken) {
        foreach (var member in Members) {
            var context = tracker.NextContext();
            try {
                tracker.Track(member.ValidateAsync(value, context, cancellationToken));
            }
            catch (Exception ex) {
                tracker.Track(Task.FromException(ex));
            }
        }

        return tracker;
    }

    private void ValidateMembersSynchronously(object value, ValidateContext context) {
        foreach (var member in Members) member.Validate(value, context);
    }

    private static async Task ValidateValidatableObjectAsync(object value, ValidateContext context,
        ValidationContext validationContext, CancellationToken cancellationToken) {
        var errorPrefix = context.CurrentValidationPath;
        switch (value) {
            case IAsyncValidatableObject asyncValidatable:
                await foreach (var validationResult in asyncValidatable
                                   .ValidateAsync(validationContext, cancellationToken)
                                   .ConfigureAwait(false))
                    ReportValidatableObjectResult(validationResult, errorPrefix, value, context);
                break;
            case IValidatableObject validatable:
                foreach (var validationResult in validatable.Validate(validationContext))
                    ReportValidatableObjectResult(validationResult, errorPrefix, value, context);
                break;
        }
    }

    private static void ReportValidatableObjectResult(ValidationResult validationResult, string errorPrefix,
        object value, ValidateContext context) {
        // IValidatableObject messages are reported as returned: they are not localized.
        if (validationResult == ValidationResult.Success || validationResult.ErrorMessage is null) return;

        var reportedMember = false;
        foreach (var memberName in validationResult.MemberNames) {
            reportedMember = true;
            context.AddValidationError(new ValidationError {
                Name = memberName,
                Path = string.IsNullOrEmpty(errorPrefix) ? memberName : $"{errorPrefix}.{memberName}",
                ErrorMessage = validationResult.ErrorMessage,
                Container = value
            });
        }

        if (reportedMember) return;

        context.AddValidationError(new ValidationError {
            Name = string.Empty,
            Path = string.Empty,
            ErrorMessage = validationResult.ErrorMessage,
            Container = value
        });
    }

    private IEnumerable<ValidatableTypeInfo> GetSuperTypeInfos(Type actualType, ValidationOptions options) {
        foreach (var @interface in _implementedInterfaces)
            if (TryGetSuperTypeInfo(@interface, actualType, options) is { } superTypeInfo)
                yield return superTypeInfo;

        var baseType = Type.BaseType;
        while (baseType is not null) {
            if (TryGetSuperTypeInfo(baseType, actualType, options) is { } superTypeInfo)
                yield return superTypeInfo;

            baseType = baseType.BaseType;
        }

        static ValidatableTypeInfo? TryGetSuperTypeInfo(Type superType, Type actualType, ValidationOptions options) {
            return superType.IsAssignableFrom(actualType)
                   && options.TryGetValidatableTypeInfo(superType, out var found)
                   && found is ValidatableTypeInfo superTypeInfo
                ? superTypeInfo
                : null;
        }
    }

    private protected override void ReportError(ValidateContext context, string displayName, object? container,
        ValidationAttribute attribute, ValidationResult result) {
        var reportedMember = false;
        foreach (var memberName in result.MemberNames) {
            reportedMember = true;
            var errorMessage =
                ResolveAttributeErrorMessage(context, memberName, displayName, Type, attribute, result);
            if (errorMessage is null) continue;

            context.AddValidationError(new ValidationError {
                Name = memberName,
                Path = string.IsNullOrEmpty(context.CurrentValidationPath)
                    ? memberName
                    : $"{context.CurrentValidationPath}.{memberName}",
                ErrorMessage = errorMessage,
                Container = container
            });
        }

        if (reportedMember) return;

        var typeErrorMessage = ResolveAttributeErrorMessage(context, null, displayName, Type, attribute, result);
        if (typeErrorMessage is null) return;

        context.AddValidationError(new ValidationError {
            Name = string.Empty,
            Path = context.CurrentValidationPath,
            ErrorMessage = typeErrorMessage,
            Container = container
        });
    }
}
#endif
