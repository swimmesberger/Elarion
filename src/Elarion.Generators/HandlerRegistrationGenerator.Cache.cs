using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private static CacheableInfo? ParseCacheable(
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        SymbolDisplayFormat fmt) {
        var cacheableAttrSymbol = compilation.GetTypeByMetadataName(CacheableAttributeMetadataName);
        if (cacheableAttrSymbol is null)
            return null;

        var attr = classSymbol.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, cacheableAttrSymbol));
        if (attr is null)
            return null;

        var tags = attr.ConstructorArguments.Length > 0
            ? ParseStringArray(attr.ConstructorArguments[0])
            : ImmutableArray<string>.Empty;
        var durationSeconds = 60;
        var scopeValue = 0;
        var requestedKeyProperties = ImmutableArray<string>.Empty;

        foreach (var namedArg in attr.NamedArguments) {
            switch (namedArg.Key) {
                case "DurationSeconds" when namedArg.Value.Value is int value:
                    durationSeconds = value;
                    break;
                case "Scope" when namedArg.Value.Value is int value:
                    scopeValue = value;
                    break;
                case "KeyProperties":
                    requestedKeyProperties = ParseStringArray(namedArg.Value);
                    break;
            }
        }

        var keyProperties = ResolveCacheKeyProperties(requestType, requestedKeyProperties);
        var resultValueFqn = TryGetResultValueFqn(responseType, fmt);
        return new CacheableInfo(tags, durationSeconds, scopeValue, keyProperties, resultValueFqn);
    }

    private static CacheInvalidationInfo? ParseCacheInvalidation(
        INamedTypeSymbol classSymbol,
        Compilation compilation) {
        var cacheInvalidateAttrSymbol = compilation.GetTypeByMetadataName(CacheInvalidateAttributeMetadataName);
        if (cacheInvalidateAttrSymbol is null)
            return null;

        var attr = classSymbol.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, cacheInvalidateAttrSymbol));
        if (attr is null)
            return null;

        var tags = attr.ConstructorArguments.Length > 0
            ? ParseStringArray(attr.ConstructorArguments[0])
            : ImmutableArray<string>.Empty;
        var scopeValue = 0;

        foreach (var namedArg in attr.NamedArguments) {
            if (namedArg.Key == "Scope" && namedArg.Value.Value is int value)
                scopeValue = value;
        }

        return new CacheInvalidationInfo(tags, scopeValue);
    }

    private static ImmutableArray<CacheDiagnosticInfo> ValidateCacheMetadata(
        INamedTypeSymbol classSymbol,
        CacheableInfo? cacheable,
        CacheInvalidationInfo? cacheInvalidation) {
        var builder = ImmutableArray.CreateBuilder<CacheDiagnosticInfo>();
        var location = classSymbol.Locations.FirstOrDefault();

        if (cacheable is not null && cacheInvalidation is not null) {
            builder.Add(new CacheDiagnosticInfo(
                CacheableAndInvalidatingDescriptor,
                location,
                [classSymbol.Name]));
        }

        if (cacheable is not null) {
            ValidateTags(classSymbol, cacheable.Tags, location, builder);
            if (cacheable.DurationSeconds <= 0) {
                builder.Add(new CacheDiagnosticInfo(
                    InvalidCacheDurationDescriptor,
                    location,
                    [classSymbol.Name]));
            }
        }

        if (cacheInvalidation is not null)
            ValidateTags(classSymbol, cacheInvalidation.Tags, location, builder);

        return builder.ToImmutable();
    }

    private static void ValidateTags(
        INamedTypeSymbol classSymbol,
        ImmutableArray<string> tags,
        Location? location,
        ImmutableArray<CacheDiagnosticInfo>.Builder builder) {
        if (tags.IsDefaultOrEmpty) {
            builder.Add(new CacheDiagnosticInfo(
                EmptyCacheTagsDescriptor,
                location,
                [classSymbol.Name]));
            return;
        }

        foreach (var tag in tags) {
            if (string.IsNullOrWhiteSpace(tag) || tag == "*") {
                builder.Add(new CacheDiagnosticInfo(
                    InvalidCacheTagDescriptor,
                    location,
                    [classSymbol.Name, tag]));
            }
        }
    }

    private static ImmutableArray<string> ParseStringArray(TypedConstant typedConstant) {
        if (typedConstant.Kind != TypedConstantKind.Array)
            return ImmutableArray<string>.Empty;

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var value in typedConstant.Values) {
            if (value.Value is string stringValue)
                builder.Add(stringValue);
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<CacheKeyPropertyInfo> ResolveCacheKeyProperties(
        ITypeSymbol requestType,
        ImmutableArray<string> requestedKeyProperties) {
        var publicProperties = requestType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p is { IsStatic: false, DeclaredAccessibility: Accessibility.Public } &&
                        p.GetMethod is not null &&
                        p.Parameters.Length == 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        if (requestedKeyProperties.IsDefaultOrEmpty) {
            return publicProperties.Values
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new CacheKeyPropertyInfo(p.Name))
                .ToImmutableArray();
        }

        var builder = ImmutableArray.CreateBuilder<CacheKeyPropertyInfo>();
        foreach (var propertyName in requestedKeyProperties) {
            if (publicProperties.ContainsKey(propertyName))
                builder.Add(new CacheKeyPropertyInfo(propertyName));
        }

        return builder.ToImmutable();
    }

    private static string? TryGetResultValueFqn(ITypeSymbol responseType, SymbolDisplayFormat fmt) {
        if (responseType is not INamedTypeSymbol namedType ||
            namedType.TypeArguments.Length != 1 ||
            namedType.OriginalDefinition.ToDisplayString() != "Elarion.Abstractions.Result<T>") {
            return null;
        }

        return namedType.TypeArguments[0].ToDisplayString(fmt);
    }
}

