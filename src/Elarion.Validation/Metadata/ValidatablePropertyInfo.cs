#if NET11_0_OR_GREATER
// Adapted from the Microsoft.Extensions.Validation 11.0.0 source-generator templates (dotnet/aspnetcore,
// src/Validation), Copyright (c) .NET Foundation and Contributors, licensed under the MIT License — see
// THIRD-PARTY-NOTICES.md. Differences from the template: the display name is a constructor string (Elarion's
// generator resolves it at compile time) instead of a localizable DisplayNameInfo, and union-valued and
// [SkipValidation] properties are not modeled because Elarion's generator does not emit them.

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Validation;

namespace Elarion.Validation.Metadata;

/// <summary>
/// Validation metadata for one property of a validatable type: runs the property's attributes (<c>[Required]</c>
/// first, then the remaining synchronous attributes, then asynchronous ones) and walks into nested validatable
/// objects and collection elements. Infrastructure for source-generated resolvers; not for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ValidatablePropertyInfo : ValidatableInfo, IValidatablePropertyInfo {
    private RequiredAttribute? _requiredAttribute;

    /// <summary>Creates the metadata for property <paramref name="name"/> of <paramref name="declaringType"/>.</summary>
    /// <param name="declaringType">The type declaring the property; its public properties are read at run time.</param>
    /// <param name="propertyType">The declared property type, used to detect collections.</param>
    /// <param name="name">The CLR property name, which is also the error-path segment.</param>
    /// <param name="displayName">The display name substituted into error messages.</param>
    protected ValidatablePropertyInfo(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
        Type declaringType,
        Type propertyType,
        string name,
        string displayName) {
        DeclaringType = declaringType;
        PropertyType = propertyType;
        Name = name;
        DisplayName = displayName;
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    internal Type DeclaringType { get; }

    internal Type PropertyType { get; }

    internal string Name { get; }

    internal string DisplayName { get; }

    private PropertyInfo Property =>
        DeclaringType.GetProperty(Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"Property '{Name}' not found on type '{DeclaringType.Name}'.");

    /// <summary>Returns the validation attributes declared on the property.</summary>
    protected abstract ValidationAttribute[] GetValidationAttributes();

    /// <inheritdoc />
    public virtual async Task ValidateAsync(object containingObject, ValidateContext context,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(containingObject);

        var propertyValue = Property.GetValue(containingObject);
        var validationAttributes = GetValidationAttributes();
        var originalPrefix = context.CurrentValidationPath;
        context.CurrentValidationPath = string.IsNullOrEmpty(originalPrefix) ? Name : $"{originalPrefix}.{Name}";

        var validationContext = new ValidationContext(containingObject, DisplayName, context.ServiceProvider, null) {
            MemberName = Name
        };

        if (!ValidateRequiredAttribute(validationAttributes, context, propertyValue, containingObject,
                validationContext)) {
            // Restore the path before the early return: siblings validated with this shared context read it.
            context.CurrentValidationPath = originalPrefix;
            return;
        }

        await ValidateAttributesAsync(context, validationAttributes, propertyValue, containingObject,
            validationContext, DisplayName, cancellationToken).ConfigureAwait(false);

        var validationOptions = context.ValidationOptions;
        ValidateDepth(context);
        context.CurrentDepth++;

        try {
            if (IsEnumerable(PropertyType) && propertyValue is IEnumerable enumerable) {
                var index = 0;
                var currentPrefix = context.CurrentValidationPath;
                var tracker = new AsyncValidationTracker(context);
                foreach (var item in enumerable) {
                    if (item is not null
                        && validationOptions.TryGetValidatableTypeInfo(item.GetType(), out var validatableType)) {
                        var currentContext = tracker.NextContext();
                        currentContext.CurrentValidationPath = $"{currentPrefix}[{index}]";
                        try {
                            tracker.Track(validatableType.ValidateAsync(item, currentContext, cancellationToken));
                        }
                        catch (Exception ex) {
                            tracker.Track(Task.FromException(ex));
                        }
                    }

                    index++;
                }

                await tracker.CompleteAsync().ConfigureAwait(false);
                context.CurrentValidationPath = currentPrefix;
            }
            else if (propertyValue is not null
                     && validationOptions.TryGetValidatableTypeInfo(propertyValue.GetType(), out var validatableType)) {
                await validatableType.ValidateAsync(propertyValue, context, cancellationToken).ConfigureAwait(false);
            }
        }
        finally {
            context.CurrentDepth--;
            context.CurrentValidationPath = originalPrefix;
        }
    }

    /// <inheritdoc />
    public virtual void Validate(object containingObject, ValidateContext context) {
        ArgumentNullException.ThrowIfNull(containingObject);

        var propertyValue = Property.GetValue(containingObject);
        var validationAttributes = GetValidationAttributes();
        var originalPrefix = context.CurrentValidationPath;
        context.CurrentValidationPath = string.IsNullOrEmpty(originalPrefix) ? Name : $"{originalPrefix}.{Name}";

        var validationContext = new ValidationContext(containingObject, DisplayName, context.ServiceProvider, null) {
            MemberName = Name
        };

        if (!ValidateRequiredAttribute(validationAttributes, context, propertyValue, containingObject,
                validationContext)) {
            context.CurrentValidationPath = originalPrefix;
            return;
        }

        ValidateAllAttributesSynchronously(context, validationAttributes, propertyValue, containingObject,
            validationContext, DisplayName);

        var validationOptions = context.ValidationOptions;
        ValidateDepth(context);
        context.CurrentDepth++;

        try {
            if (IsEnumerable(PropertyType) && propertyValue is IEnumerable enumerable) {
                var index = 0;
                var currentPrefix = context.CurrentValidationPath;
                foreach (var item in enumerable) {
                    if (item is not null
                        && validationOptions.TryGetValidatableTypeInfo(item.GetType(), out var validatableType)) {
                        context.CurrentValidationPath = $"{currentPrefix}[{index}]";
                        validatableType.Validate(item, context);
                    }

                    index++;
                }

                context.CurrentValidationPath = currentPrefix;
            }
            else if (propertyValue is not null
                     && validationOptions.TryGetValidatableTypeInfo(propertyValue.GetType(), out var validatableType)) {
                validatableType.Validate(propertyValue, context);
            }
        }
        finally {
            context.CurrentDepth--;
            context.CurrentValidationPath = originalPrefix;
        }
    }

    private void ValidateDepth(ValidateContext context) {
        if (context.CurrentDepth >= context.ValidationOptions.MaxDepth)
            throw new InvalidOperationException(
                $"Maximum validation depth of {context.ValidationOptions.MaxDepth} exceeded at "
                + $"'{context.CurrentValidationPath}' in '{DeclaringType.Name}.{Name}'. This is likely caused by a "
                + "circular reference in the object graph. Consider increasing the MaxDepth in ValidationOptions if "
                + "deeper validation is required.");
    }

    private bool ValidateRequiredAttribute(ValidationAttribute[] validationAttributes, ValidateContext context,
        object? propertyValue, object containingObject, ValidationContext validationContext) {
        if (_requiredAttribute is null && !TryGetRequiredAttribute(validationAttributes, out _requiredAttribute))
            return true;

        var result = _requiredAttribute!.GetValidationResult(propertyValue, validationContext);
        if (result is null || result == ValidationResult.Success) return true;

        ReportError(context, validationContext.DisplayName, containingObject, _requiredAttribute, result);
        return false;
    }

    private protected override void ReportError(ValidateContext context, string displayName, object? container,
        ValidationAttribute attribute, ValidationResult result) {
        var errorMessage = ResolveAttributeErrorMessage(context, Name, displayName, DeclaringType, attribute, result);
        if (errorMessage is null) return;

        context.AddValidationError(new ValidationError {
            Name = Name,
            Path = context.CurrentValidationPath,
            ErrorMessage = errorMessage,
            Container = container
        });
    }
}
#endif
