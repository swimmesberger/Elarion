#if NET11_0_OR_GREATER
// Adapted from the Microsoft.Extensions.Validation 11.0.0 source-generator templates (dotnet/aspnetcore,
// src/Validation), Copyright (c) .NET Foundation and Contributors, licensed under the MIT License — see
// THIRD-PARTY-NOTICES.md.

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Validation;

namespace Elarion.Validation.Metadata;

/// <summary>
/// Error-message localization of the validation walker: the same resource-key lookup order as
/// Microsoft.Extensions.Validation 11 (explicit <see cref="ValidationAttribute.ErrorMessage"/> key, then
/// <c>{Type}_{Member}_{Attribute}_Error</c>, <c>{Type}_{Attribute}_Error</c>, <c>{Attribute}_Error</c>).
/// </summary>
internal static class LocalizationHelpers {
    public static IStringLocalizer CreateStringLocalizer(ValidateContext context, Type type,
        IStringLocalizerFactory factory) {
        return context.ValidationOptions.LocalizerProvider(type, factory)
               ?? throw new InvalidOperationException(
                   $"The ValidationOptions.LocalizerProvider delegate returned null for type '{type.FullName}'. "
                   + "The delegate must return a non-null IStringLocalizer instance.");
    }

    public static string? FindLocalizedTemplate(IStringLocalizer localizer, ValidationAttribute attribute,
        string? memberName, Type declaringType) {
        if (!string.IsNullOrEmpty(attribute.ErrorMessage)) {
            var explicitMatch = localizer[attribute.ErrorMessage];
            if (!explicitMatch.ResourceNotFound) return explicitMatch.Value;
        }

        var attributeName = attribute.GetType().Name;
        var typeName = GetKeySegment(declaringType);

        if (memberName is not null) {
            var memberMatch = localizer[$"{typeName}_{memberName}_{attributeName}_Error"];
            if (!memberMatch.ResourceNotFound) return memberMatch.Value;
        }

        var typeMatch = localizer[$"{typeName}_{attributeName}_Error"];
        if (!typeMatch.ResourceNotFound) return typeMatch.Value;

        var globalMatch = localizer[$"{attributeName}_Error"];
        return globalMatch.ResourceNotFound ? null : globalMatch.Value;
    }

    private static string GetKeySegment(Type type) {
        var name = (Nullable.GetUnderlyingType(type) ?? type).Name;
        var arityIndex = name.IndexOf('`');

        return arityIndex < 0 ? name : name[..arityIndex];
    }
}
#endif
