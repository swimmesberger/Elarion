using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private static CacheableInfo? ParseCacheable(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        SymbolDisplayFormat fmt,
        bool isEventConsumer,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var cacheableAttrSymbol = compilation.GetTypeByMetadataName(CacheableAttributeMetadataName);
        if (cacheableAttrSymbol is null)
            return null;

        var attr = classSymbol.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, cacheableAttrSymbol));
        if (attr is null)
            return null;

        // An event consumer is a fan-out subscriber returning Result<Unit>: caching it would cache the fan-out
        // result so a legitimate re-delivery silently skips the side effect. Report ELCACHE005 and do not attach.
        // ([CacheInvalidate] on a consumer is legitimate — reacting to an event by evicting caches — so it stays.)
        if (isEventConsumer) {
            diagnostics.Add(DiagnosticInfo.Create(
                CacheableOnEventConsumerDescriptor,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(fmt),
                requestType.ToDisplayString(fmt)));
            return null;
        }

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

        var keyProperties = ResolveCacheKeyProperties(
            classDecl, classSymbol, requestType, requestedKeyProperties, fmt, diagnostics);
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
        // Mirror CacheInvalidateAttribute.Scope's default of HandlerCacheScope.Global (= 1): the mutating caller is
        // usually not the user whose cached read must be evicted, so over-invalidation is the safe default. (The
        // generator cannot reference the Abstractions enum, hence the literal. Cacheable stays CurrentUser = 0.)
        var scopeValue = 1;

        foreach (var namedArg in attr.NamedArguments) {
            if (namedArg.Key == "Scope" && namedArg.Value.Value is int value)
                scopeValue = value;
        }

        return new CacheInvalidationInfo(tags, scopeValue);
    }

    private static ImmutableArray<DiagnosticInfo> ValidateCacheMetadata(
        INamedTypeSymbol classSymbol,
        CacheableInfo? cacheable,
        CacheInvalidationInfo? cacheInvalidation) {
        var builder = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var location = classSymbol.Locations.FirstOrDefault();

        if (cacheable is not null && cacheInvalidation is not null) {
            builder.Add(DiagnosticInfo.Create(
                CacheableAndInvalidatingDescriptor,
                location,
                classSymbol.Name));
        }

        if (cacheable is not null) {
            ValidateTags(classSymbol, cacheable.Tags, location, builder);
            if (cacheable.DurationSeconds <= 0) {
                builder.Add(DiagnosticInfo.Create(
                    InvalidCacheDurationDescriptor,
                    location,
                    classSymbol.Name));
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
        ImmutableArray<DiagnosticInfo>.Builder builder) {
        if (tags.IsDefaultOrEmpty) {
            builder.Add(DiagnosticInfo.Create(
                EmptyCacheTagsDescriptor,
                location,
                classSymbol.Name));
            return;
        }

        foreach (var tag in tags) {
            if (string.IsNullOrWhiteSpace(tag) || tag == "*") {
                builder.Add(DiagnosticInfo.Create(
                    InvalidCacheTagDescriptor,
                    location,
                    classSymbol.Name, tag));
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
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ImmutableArray<string> requestedKeyProperties,
        SymbolDisplayFormat fmt,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var publicProperties = requestType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p is { IsStatic: false, DeclaredAccessibility: Accessibility.Public } &&
                        p.GetMethod is not null &&
                        p.Parameters.Length == 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        var location = classDecl.Identifier.GetLocation();
        var handlerName = classSymbol.ToDisplayString(fmt);

        // Every property that participates in the key must have a stable, injective formatting in HandlerCacheKey;
        // otherwise it falls back to object.ToString() (e.g. "System.Int32[]" for an array), so two distinct
        // requests share a key — a cross-request cache leak. Reject an unsupported key property type (ELCACHE006).
        var builder = ImmutableArray.CreateBuilder<CacheKeyPropertyInfo>();

        if (requestedKeyProperties.IsDefaultOrEmpty) {
            foreach (var property in publicProperties.Values.OrderBy(p => p.Name, StringComparer.Ordinal)) {
                if (IsSupportedCacheKeyType(property.Type)) {
                    builder.Add(new CacheKeyPropertyInfo(property.Name));
                    continue;
                }

                diagnostics.Add(DiagnosticInfo.Create(
                    UnsupportedCacheKeyPropertyTypeDescriptor,
                    location,
                    handlerName,
                    property.Name,
                    property.Type.ToDisplayString(fmt)));
            }

            return builder.ToImmutable();
        }

        foreach (var propertyName in requestedKeyProperties) {
            // ELCACHE007: a KeyProperties name with no matching request property would be silently dropped.
            if (!publicProperties.TryGetValue(propertyName, out var property)) {
                diagnostics.Add(DiagnosticInfo.Create(
                    CacheKeyPropertyNotFoundDescriptor,
                    location,
                    handlerName,
                    propertyName,
                    requestType.ToDisplayString(fmt)));
                continue;
            }

            if (IsSupportedCacheKeyType(property.Type)) {
                builder.Add(new CacheKeyPropertyInfo(propertyName));
                continue;
            }

            diagnostics.Add(DiagnosticInfo.Create(
                UnsupportedCacheKeyPropertyTypeDescriptor,
                location,
                handlerName,
                propertyName,
                property.Type.ToDisplayString(fmt)));
        }

        return builder.ToImmutable();
    }

    // The sound cache-key whitelist: scalar types that HandlerCacheKey.Part formats stably and injectively.
    // Nullable<T> unwraps to its underlying type; everything else (collections, arrays, custom classes/records)
    // is rejected so it never silently ToString()-collides.
    private static bool IsSupportedCacheKeyType(ITypeSymbol type) {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable &&
            nullable.TypeArguments.Length == 1) {
            return IsSupportedCacheKeyType(nullable.TypeArguments[0]);
        }

        if (type.TypeKind == TypeKind.Enum)
            return true;

        switch (type.SpecialType) {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Decimal:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_String:
                return true;
        }

        return type.ToDisplayString() switch {
            "System.Guid" => true,
            "System.DateTime" => true,
            "System.DateTimeOffset" => true,
            "System.DateOnly" => true,
            "System.TimeOnly" => true,
            "System.TimeSpan" => true,
            _ => false,
        };
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

