using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Members visible by permission (<c>[VisibleWith]</c>, ADR-0103): finds, at compile time, every member of a handler's
/// response that only holders of a permission may see — through nested records, structs, arrays, lists, read-only
/// collections, <c>Nullable&lt;T&gt;</c> and <c>ElarionCreated&lt;T&gt;</c> — and emits one copy function per type on
/// the way, which the <c>VisibilityDecorator</c> applies for a caller who does not hold every permission. Anything the
/// copy cannot pass through is a build error, never a silently disclosed member.
/// </summary>
public sealed partial class HandlerRegistrationGenerator {
    private const string VisibleWithAttributeMetadataName = "Elarion.Abstractions.Authorization.VisibleWithAttribute";

    private const string VisibilityPermissionCheckerFqn =
        "global::Elarion.Abstractions.Authorization.IPermissionChecker";

    internal static class RedactorKind {
        public const int Object = 0;
        public const int Nullable = 1;
        public const int Array = 2;
        public const int List = 3;
        public const int Sequence = 4;
        public const int ImmutableArray = 5;
        public const int Created = 6;
        public const int Polymorphic = 7;
    }

    /// <summary>The members a handler's response shows by permission: the success value, every permission named (as the
    /// C# expressions the generated code checks), and the copy functions (index 0 copies the value itself).</summary>
    internal sealed record VisibilityInfo(
        string ValueFqn,
        EquatableArray<string> Permissions,
        EquatableArray<RedactorModel> Redactors);

    /// <summary>One emitted copy function: an object copied with <c>with</c>, or a wrapper/collection of
    /// <see cref="Element"/>.</summary>
    internal sealed record RedactorModel(
        int Kind,
        string TypeFqn,
        bool IsReferenceType,
        int Element,
        EquatableArray<RedactedMemberModel> Members,
        // For a polymorphic type ([JsonDerivedType]): the copy functions of the derived types to dispatch to,
        // most-derived first; the members above then copy an instance of the declared type itself.
        EquatableArray<int> Derived);

    /// <summary>A member rewritten in the copy: hidden unless one of <see cref="Permissions"/> is held (none: never
    /// hidden), copied through <see cref="Redactor"/> (-1: as is), or both. Each permission is the C# expression the
    /// generated code passes to <c>Holds</c>: a string literal, or a reference to an <c>ElarionPermissions</c>
    /// constant.</summary>
    internal sealed record RedactedMemberModel(
        string Name,
        EquatableArray<string> Permissions,
        int Redactor);

    private static readonly DiagnosticDescriptor VisibleMemberNotNullable = new(
        "ELAUTH003",
        "[VisibleWith] member is not nullable",
        "Member '{0}' of '{1}' is marked [VisibleWith({2})] but its type '{3}' is not nullable, so it cannot be "
        + "hidden from callers without the permission; make the member nullable",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor VisibleMemberCannotBeHidden = new(
        "ELAUTH004",
        "Response member visible by permission cannot be hidden",
        "Handler '{0}' answers '{1}', which holds members visible only with a permission ([VisibleWith]), but {2}; "
        + "reach such members through records or structs with settable (init) members, arrays, List<T>, "
        + "ImmutableArray<T>, the read-only collection interfaces and Result<T>",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        true);

    // A permission argument no generator can read must never leave its member visible: ELAUTH005, never a skip.
    private static readonly DiagnosticDescriptor VisiblePermissionUnreadable = new(
        "ELAUTH005",
        "[VisibleWith] permission cannot be read",
        "Member '{0}' of '{1}' is marked [VisibleWith] with no permission or one the generator cannot read, so it cannot "
        + "hide the member; write each permission as a string constant, as ElarionPermissions.Resource.Verb, or as "
        + "(resource, verb)",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        true);

    // Stands for a [VisibleWith] whose permission could not be read: the member counts as marked, so the walk reaches
    // it and reports ELAUTH005. Never a C# expression, so it cannot reach the generated code.
    private const string UnreadablePermission = "<unreadable>";

    private static readonly string[] Unreadable = [UnreadablePermission];

    private const string AnyOfPropertyName = "AnyOf";

    private const string GeneratedPermissionCatalogName = "ElarionPermissions";

    /// <summary>The per-pass walk state: type nodes memoized across every handler of one resolution pass.</summary>
    internal sealed class VisibilityWalk(
        INamedTypeSymbol visibleWithAttribute,
        Compilation compilation,
        string rootNamespace) {
        public INamedTypeSymbol VisibleWithAttribute { get; } = visibleWithAttribute;
        public Compilation Compilation { get; } = compilation;
        public IAssemblySymbol CurrentAssembly { get; } = compilation.Assembly;

        // Where this compilation's ElarionPermissions is generated (PermissionCatalogGenerator).
        public string RootNamespace { get; } = rootNamespace;
        public Dictionary<ITypeSymbol, VisibilityNode> Nodes { get; } = new(SymbolEqualityComparer.Default);
    }

    internal sealed class VisibilityNode(ITypeSymbol type) {
        public ITypeSymbol Type { get; } = type;

        // -1: a leaf (nothing inside can be visible by permission); otherwise a RedactorKind, or Unsupported.
        public int Kind { get; set; } = -1;
        public VisibilityNode? Element { get; set; }
        // Permissions: null when the member is not marked; otherwise any one of them shows it.
        public List<(IPropertySymbol Property, string[]? Permissions, VisibilityNode Node)> Members { get; } = [];
        public List<VisibilityNode> Derived { get; } = [];
        public string? UnsupportedReason { get; set; }
    }

    private const int UnsupportedKind = 99;

    private static VisibilityInfo? ParseVisibility(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol responseType,
        VisibilityWalk? walk,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (walk is null)
            return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var isResult = responseType is INamedTypeSymbol { TypeArguments.Length: 1 } namedResponse
                       && IsDefinition(namedResponse.OriginalDefinition, "Elarion.Abstractions", "Result`1");
        var valueType = isResult ? ((INamedTypeSymbol)responseType).TypeArguments[0] : responseType;

        var root = VisitVisibility(valueType, walk);
        var closure = new List<VisibilityNode>();
        CollectVisibilityClosure(root, closure, new HashSet<VisibilityNode>());
        var needs = ComputeNeeds(closure);
        if (!needs.Contains(root))
            return null;

        var handlerFqn = classSymbol.ToDisplayString(fmt);
        var responseFqn = responseType.ToDisplayString(fmt);
        var location = classDecl.Identifier.GetLocation();
        var failed = false;

        void Fail(string reason) {
            diagnostics.Add(DiagnosticInfo.Create(VisibleMemberCannotBeHidden, location, handlerFqn, responseFqn,
                reason));
            failed = true;
        }

        if (!isResult)
            Fail("the response is not Result<T>, so the pipeline cannot answer a copy");

        // Number the needed nodes in a deterministic depth-first order from the root, and check each can be copied.
        var ids = new Dictionary<VisibilityNode, int>();
        var order = new List<VisibilityNode>();

        void Number(VisibilityNode node) {
            if (!needs.Contains(node) || ids.ContainsKey(node))
                return;

            ids[node] = order.Count;
            order.Add(node);
            if (node.Element is { } element)
                Number(element);

            foreach (var member in node.Members)
                Number(member.Node);

            foreach (var derived in node.Derived)
                Number(derived);
        }

        Number(root);

        var permissions = new SortedSet<string>(StringComparer.Ordinal);
        var redactors = ImmutableArray.CreateBuilder<RedactorModel>(order.Count);
        foreach (var node in order) {
            var typeFqn = node.Type.ToDisplayString(fmt);
            if (node.Kind == UnsupportedKind) {
                Fail($"'{typeFqn}' {node.UnsupportedReason}");
                continue;
            }

            if (!AttributeArgumentRendering.IsAccessibleFromGeneratedCode(node.Type, walk.CurrentAssembly)) {
                Fail($"'{typeFqn}' is not accessible from the handler's assembly");
                continue;
            }

            var members = ImmutableArray.CreateBuilder<RedactedMemberModel>();
            var hasMembersToRewrite = node.Members.Any(m => m.Permissions is not null || needs.Contains(m.Node));
            if (node.Kind == RedactorKind.Object
                || (node.Kind == RedactorKind.Polymorphic && hasMembersToRewrite)) {
                if (node.Type is INamedTypeSymbol { IsRecord: false, IsValueType: false })
                    Fail($"'{typeFqn}' is neither a record nor a struct, so it cannot be copied with a 'with' expression");

                foreach (var (property, memberPermissions, memberNode) in node.Members) {
                    var nested = needs.Contains(memberNode);
                    if (memberPermissions is null && !nested)
                        continue;

                    if (memberPermissions is not null && memberPermissions.Contains(UnreadablePermission)) {
                        diagnostics.Add(DiagnosticInfo.Create(VisiblePermissionUnreadable, location,
                            property.Name, typeFqn));
                        failed = true;
                        continue;
                    }

                    if (memberPermissions is not null) {
                        foreach (var permission in memberPermissions)
                            permissions.Add(permission);

                        if (!IsNullable(property.Type)) {
                            diagnostics.Add(DiagnosticInfo.Create(VisibleMemberNotNullable, location,
                                property.Name, typeFqn, string.Join(", ", memberPermissions),
                                property.Type.ToDisplayString(fmt)));
                            failed = true;
                            continue;
                        }
                    }

                    if (!HasAccessibleSetter(property, walk.CurrentAssembly)) {
                        Fail($"member '{property.Name}' of '{typeFqn}' has no accessible init or set accessor");
                        continue;
                    }

                    members.Add(new RedactedMemberModel(property.Name,
                        (memberPermissions ?? []).ToImmutableArray().ToEquatableArray(),
                        nested ? ids[memberNode] : -1));
                }
            }

            // The derived types to dispatch to, most-derived first, so a type deriving from another listed one is
            // matched before its base.
            var derived = node.Derived
                .Where(needs.Contains)
                .OrderByDescending(static d => InheritanceDepth(d.Type))
                .ThenBy(d => d.Type.ToDisplayString(fmt), StringComparer.Ordinal)
                .Select(d => ids[d])
                .ToImmutableArray();

            redactors.Add(new RedactorModel(
                node.Kind,
                typeFqn,
                !node.Type.IsValueType,
                node.Element is { } element && ids.TryGetValue(element, out var elementId) ? elementId : -1,
                members.ToImmutable(),
                derived));
        }

        if (failed)
            return null;

        return new VisibilityInfo(valueType.ToDisplayString(fmt), permissions.ToImmutableArray(),
            redactors.ToImmutable());
    }

    private static VisibilityNode VisitVisibility(ITypeSymbol type, VisibilityWalk walk) {
        if (walk.Nodes.TryGetValue(type, out var existing))
            return existing;

        var node = new VisibilityNode(type);
        walk.Nodes[type] = node;

        if (type is IArrayTypeSymbol array) {
            node.Kind = array.Rank == 1 ? RedactorKind.Array : UnsupportedKind;
            node.UnsupportedReason = "is a multi-dimensional array";
            node.Element = VisitVisibility(array.ElementType, walk);
            return node;
        }

        if (type is not INamedTypeSymbol named
            || named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
            return node;

        var definition = named.OriginalDefinition;
        if (definition.SpecialType == SpecialType.System_Nullable_T) {
            node.Kind = RedactorKind.Nullable;
            node.Element = VisitVisibility(named.TypeArguments[0], walk);
            return node;
        }

        var wrapperKind = definition.SpecialType switch {
            SpecialType.System_Collections_Generic_IEnumerable_T
                or SpecialType.System_Collections_Generic_IReadOnlyList_T
                or SpecialType.System_Collections_Generic_IReadOnlyCollection_T
                or SpecialType.System_Collections_Generic_IList_T
                or SpecialType.System_Collections_Generic_ICollection_T => RedactorKind.Sequence,
            _ when IsDefinition(definition, "System.Collections.Generic", "List`1") => RedactorKind.List,
            _ when IsDefinition(definition, "System.Collections.Immutable", "ImmutableArray`1") =>
                RedactorKind.ImmutableArray,
            _ when IsDefinition(definition, "Elarion.Abstractions", "ElarionCreated`1") => RedactorKind.Created,
            _ => -1
        };
        if (wrapperKind < 0 && (named.SpecialType != SpecialType.None || named.IsAnonymousType))
            return node;

        if (wrapperKind >= 0) {
            node.Kind = wrapperKind;
            node.Element = VisitVisibility(named.TypeArguments[0], walk);
            return node;
        }

        // Any other collection (a dictionary's values included): walk its element so a marked member inside is
        // reported, not silently passed.
        if (GetEnumerableElementType(named) is { } elementType) {
            if (elementType is INamedTypeSymbol { TypeArguments.Length: 2 } pair
                && IsDefinition(pair.OriginalDefinition, "System.Collections.Generic", "KeyValuePair`2"))
                elementType = pair.TypeArguments[1];

            node.Kind = UnsupportedKind;
            node.UnsupportedReason = "is a collection type the pipeline cannot copy";
            node.Element = VisitVisibility(elementType, walk);
            return node;
        }

        // Framework and BCL types are leaves: they never carry an application's [VisibleWith] members.
        var ns = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.",
                StringComparison.Ordinal))
            return node;

        // A polymorphic type: System.Text.Json writes a derived type's own members only for the types its
        // [JsonDerivedType] attributes declare, so exactly those are followed, and the copy dispatches on them.
        var derivedTypes = ReadDerivedTypes(named);
        if (derivedTypes.Count > 0) {
            node.Kind = RedactorKind.Polymorphic;
            foreach (var derivedType in derivedTypes)
                node.Derived.Add(VisitVisibility(derivedType, walk));

            // An instance of the declared type itself (when it can have one) is copied through its own members.
            if (named.TypeKind != TypeKind.Interface && !named.IsAbstract)
                foreach (var property in CollectVisibilityProperties(named))
                    node.Members.Add((property, ReadVisibleWith(property, walk), VisitVisibility(property.Type, walk)));

            return node;
        }

        foreach (var property in CollectVisibilityProperties(named))
            node.Members.Add((property, ReadVisibleWith(property, walk), VisitVisibility(property.Type, walk)));

        // Without [JsonDerivedType] an interface is written through its declared members, but no copy of it can be
        // made; one that reaches a marked member is reported.
        if (named.TypeKind == TypeKind.Interface) {
            node.Kind = UnsupportedKind;
            node.UnsupportedReason =
                "is an interface the pipeline cannot copy; declare its implementations with [JsonDerivedType]";
            return node;
        }

        node.Kind = RedactorKind.Object;
        return node;
    }

    private static void CollectVisibilityClosure(VisibilityNode node, List<VisibilityNode> closure,
        HashSet<VisibilityNode> seen) {
        if (!seen.Add(node))
            return;

        closure.Add(node);
        if (node.Element is { } element)
            CollectVisibilityClosure(element, closure, seen);

        foreach (var member in node.Members)
            CollectVisibilityClosure(member.Node, closure, seen);

        foreach (var derived in node.Derived)
            CollectVisibilityClosure(derived, closure, seen);
    }

    // A fixpoint over the closure, so a cycle (a tree node holding a list of itself) cannot cut a path short.
    private static HashSet<VisibilityNode> ComputeNeeds(List<VisibilityNode> closure) {
        var needs = new HashSet<VisibilityNode>();
        bool changed;
        do {
            changed = false;
            foreach (var node in closure) {
                if (needs.Contains(node))
                    continue;

                var need = node.Element is { } element && needs.Contains(element);
                foreach (var member in node.Members)
                    need |= member.Permissions is not null || needs.Contains(member.Node);

                foreach (var derived in node.Derived)
                    need |= needs.Contains(derived);

                if (need && needs.Add(node))
                    changed = true;
            }
        } while (changed);

        return needs;
    }

    private const string JsonDerivedTypeAttributeMetadataName =
        "System.Text.Json.Serialization.JsonDerivedTypeAttribute";

    private static List<INamedTypeSymbol> ReadDerivedTypes(INamedTypeSymbol type) {
        var derived = new List<INamedTypeSymbol>();
        foreach (var attribute in type.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == JsonDerivedTypeAttributeMetadataName
                && attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is INamedTypeSymbol derivedType)
                derived.Add(derivedType);

        return derived;
    }

    private static int InheritanceDepth(ITypeSymbol type) {
        var depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            depth++;

        return depth;
    }

    private static bool IsDefinition(INamedTypeSymbol definition, string ns, string metadataName) {
        return definition.MetadataName == metadataName
               && definition.ContainingNamespace?.ToDisplayString() == ns;
    }

    private static ITypeSymbol? GetEnumerableElementType(INamedTypeSymbol type) {
        foreach (var iface in type.AllInterfaces)
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return iface.TypeArguments[0];

        return null;
    }

    // Public, gettable instance properties (no indexers), most-derived first.
    private static List<IPropertySymbol> CollectVisibilityProperties(INamedTypeSymbol type) {
        var result = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (ITypeSymbol? current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            foreach (var member in current.GetMembers()) {
                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, GetMethod: not null } property)
                    continue;

                if (property.DeclaredAccessibility != Accessibility.Public || property.IsImplicitlyDeclared)
                    continue;

                // A record's compiler-generated EqualityContract is protected, so it never reaches here.
                if (seen.Add(property.Name))
                    result.Add(property);
            }

        return result;
    }

    // The property's [VisibleWith], or — for a positional record — the one on the same-named constructor parameter.
    private static string[]? ReadVisibleWith(IPropertySymbol property, VisibilityWalk walk) {
        foreach (var attribute in property.GetAttributes())
            if (ReadPermissions(attribute, walk) is { } permissions)
                return permissions;

        if (property.ContainingType is not { } declaringType)
            return null;

        foreach (var constructor in declaringType.InstanceConstructors)
        foreach (var parameter in constructor.Parameters) {
            if (!string.Equals(parameter.Name, property.Name, StringComparison.Ordinal))
                continue;

            foreach (var attribute in parameter.GetAttributes())
                if (ReadPermissions(attribute, walk) is { } permissions)
                    return permissions;
        }

        return null;
    }

    // The permissions of a [VisibleWith] (the constructor's, then AnyOf), any one of which shows the member, as the C#
    // expressions the generated code checks; Unreadable when one cannot be read or none is named; null when the
    // attribute is not [VisibleWith].
    private static string[]? ReadPermissions(AttributeData attribute, VisibilityWalk walk) {
        if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, walk.VisibleWithAttribute))
            return null;

        // An argument naming a constant another generator emits into this compilation does not bind here
        // (AttributeBinding). The permission catalog is the one such constant the attribute documents, so the
        // arguments are read from the syntax and the catalog is referenced from the generated code, where it exists.
        if (AttributeBinding.HasUnboundArgument(attribute))
            return ReadPermissionsFromSyntax(attribute, walk) ?? Unreadable;

        var permissions = new List<string>();
        var arguments = attribute.ConstructorArguments;
        switch (arguments.Length) {
            case 0:
                break;
            case 1 when arguments[0].Value is string permission:
                permissions.Add(FormatStringLiteral(permission));
                break;
            case 2 when arguments[0].Value is string resource && arguments[1].Value is string verb:
                permissions.Add(FormatStringLiteral(resource + "." + verb));
                break;
            default:
                return Unreadable;
        }

        foreach (var named in attribute.NamedArguments) {
            if (named.Key != AnyOfPropertyName || named.Value.IsNull)
                continue;

            foreach (var value in named.Value.Values) {
                if (value.Value is not string alternative)
                    return Unreadable;

                permissions.Add(FormatStringLiteral(alternative));
            }
        }

        return permissions.Count == 0 ? Unreadable : permissions.Distinct(StringComparer.Ordinal).ToArray();
    }

    // The permissions of a [VisibleWith] whose arguments did not all bind: each read as a constant where it is one, or as
    // a reference to the generated catalog; null when any cannot be read.
    private static string[]? ReadPermissionsFromSyntax(AttributeData attribute, VisibilityWalk walk) {
        if (attribute.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax syntax)
            return null;

        var model = walk.Compilation.GetSemanticModel(syntax.SyntaxTree);
        var arguments = syntax.ArgumentList?.Arguments ?? default;
        var positional = arguments.Where(static argument => argument.NameEquals is null).ToList();
        var permissions = new List<string>();
        switch (positional.Count) {
            case 0:
                break;
            case 1:
                if (ReadPermissionExpression(positional[0].Expression, model, walk) is not { } permission)
                    return null;

                permissions.Add(permission);
                break;
            case 2:
                if (model.GetConstantValue(positional[0].Expression) is not { HasValue: true, Value: string resource }
                    || model.GetConstantValue(positional[1].Expression) is not { HasValue: true, Value: string verb })
                    return null;

                permissions.Add(FormatStringLiteral(resource + "." + verb));
                break;
            default:
                return null;
        }

        foreach (var argument in arguments) {
            if (argument.NameEquals?.Name.Identifier.ValueText != AnyOfPropertyName)
                continue;

            if (ArrayElements(argument.Expression) is not { } elements)
                return null;

            foreach (var element in elements) {
                if (ReadPermissionExpression(element, model, walk) is not { } permission)
                    return null;

                permissions.Add(permission);
            }
        }

        return permissions.Count == 0 ? null : permissions.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? ReadPermissionExpression(ExpressionSyntax expression, SemanticModel model,
        VisibilityWalk walk) {
        return model.GetConstantValue(expression) is { HasValue: true, Value: string permission }
            ? FormatStringLiteral(permission)
            : ReferenceGeneratedPermission(expression, walk);
    }

    // The elements of an array written in an attribute: a collection expression or an array creation.
    private static List<ExpressionSyntax>? ArrayElements(ExpressionSyntax expression) {
        return expression switch {
            CollectionExpressionSyntax collection when collection.Elements.All(static e => e is ExpressionElementSyntax) =>
                collection.Elements.Cast<ExpressionElementSyntax>().Select(static e => e.Expression).ToList(),
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions.ToList(),
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.ToList(),
            _ => null
        };
    }

    // `ElarionPermissions.Payments.Read` (the catalog optionally qualified) as a reference to the constant in the
    // namespace PermissionCatalogGenerator emits it into; null for any other expression. A name the catalog does not
    // have fails to compile in the attribute itself, so the reference cannot hide a typo.
    private static string? ReferenceGeneratedPermission(ExpressionSyntax expression, VisibilityWalk walk) {
        if (expression is not MemberAccessExpressionSyntax {
                Name: IdentifierNameSyntax verb,
                Expression: MemberAccessExpressionSyntax { Name: IdentifierNameSyntax resource } catalogAccess
            })
            return null;

        var catalog = catalogAccess.Expression switch {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
            AliasQualifiedNameSyntax { Name: IdentifierNameSyntax name } => name.Identifier.ValueText,
            _ => null
        };
        if (!string.Equals(catalog, GeneratedPermissionCatalogName, StringComparison.Ordinal))
            return null;

        var prefix = walk.RootNamespace.Length == 0 ? "global::" : "global::" + walk.RootNamespace + ".";
        return prefix + GeneratedPermissionCatalogName + "." + resource.Identifier.Text + "." + verb.Identifier.Text;
    }

    private static bool IsNullable(ITypeSymbol type) {
        return type.IsValueType
            ? type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            : type.NullableAnnotation != NullableAnnotation.NotAnnotated;
    }

    private static bool HasAccessibleSetter(IPropertySymbol property, IAssemblySymbol currentAssembly) {
        if (property.SetMethod is not { } setter)
            return false;

        return setter.DeclaredAccessibility switch {
            Accessibility.Public => true,
            Accessibility.Internal or Accessibility.ProtectedOrInternal =>
                SymbolEqualityComparer.Default.Equals(property.ContainingAssembly, currentAssembly),
            _ => false
        };
    }

    // --- Emission -------------------------------------------------------------------------------------------------

    private static void AppendVisibilityDecorator(StringBuilder sb, HandlerInfo handler) {
        if (handler.Visibility is not { } visibility)
            return;

        sb.AppendLine(
            $"                handler = new global::Elarion.Pipeline.VisibilityDecorator<{handler.RequestFqn}, {visibility.ValueFqn}>(");
        sb.AppendLine("                    handler,");
        sb.AppendLine($"                    sp.GetService<{VisibilityPermissionCheckerFqn}>(),");
        sb.AppendLine("                    __visiblePermissions,");
        sb.AppendLine("                    __Hide0);");
        AppendPipelineStep(sb, "                ", "global::Elarion.Pipeline.VisibilityDecorator<,>", false);
    }

    // The `{ Member = …, }` initializer of a `with` copy: each member hidden unless its permission is held, copied
    // through its own copy function, or both. The caller appends what follows the closing brace.
    private static void AppendHiddenMembers(StringBuilder sb, RedactorModel redactor, string indent) {
        sb.AppendLine($"{indent}{{");
        foreach (var member in redactor.Members) {
            var copied = member.Redactor >= 0
                ? $"__Hide{member.Redactor}(value.{member.Name}, permissions)"
                : $"value.{member.Name}";
            var permissions = member.Permissions.AsImmutableArray;
            var holds = string.Join(" || ", permissions.Select(static p => $"permissions.Holds({p})"));
            var expression = permissions.IsDefaultOrEmpty
                ? copied
                : permissions.Length == 1
                    ? $"{holds} ? {copied} : null"
                    : $"({holds}) ? {copied} : null";
            sb.AppendLine($"{indent}    {member.Name} = {expression},");
        }

        sb.Append($"{indent}}}");
    }

    // The permissions and the copy functions, emitted into the registration class. The functions are written without
    // nullable annotations (their own #nullable context), because a member a caller may not see is set to null in a
    // copy of a value the handler declared non-null.
    private static void AppendVisibilityMembers(StringBuilder sb, HandlerInfo handler) {
        if (handler.Visibility is not { } visibility)
            return;

        sb.AppendLine();
        sb.Append("    private static readonly string[] __visiblePermissions = new string[] { ");
        sb.Append(string.Join(", ", visibility.Permissions.AsImmutableArray));
        sb.AppendLine(" };");
        sb.AppendLine();
        sb.AppendLine("#nullable disable");
        var redactors = visibility.Redactors.AsImmutableArray;
        for (var i = 0; i < redactors.Length; i++) {
            var redactor = redactors[i];
            if (i > 0) sb.AppendLine();
            sb.AppendLine(
                $"    private static {redactor.TypeFqn} __Hide{i}({redactor.TypeFqn} value, {VisibilityPermissionCheckerFqn} permissions)");
            sb.AppendLine("    {");
            if (redactor.Kind == RedactorKind.Object) {
                if (redactor.IsReferenceType)
                    sb.AppendLine("        if (value is null) return null;");

                sb.AppendLine("        return value with");
                AppendHiddenMembers(sb, redactor, "        ");
                sb.AppendLine(";");
            }
            else if (redactor.Kind == RedactorKind.Polymorphic) {
                if (redactor.IsReferenceType)
                    sb.AppendLine("        if (value is null) return null;");

                // Most-derived first, then the declared type's own instance.
                sb.AppendLine("        return value switch");
                sb.AppendLine("        {");
                foreach (var derivedId in redactor.Derived)
                    sb.AppendLine(
                        $"            {redactors[derivedId].TypeFqn} __derived => __Hide{derivedId}(__derived, permissions),");

                if (redactor.Members.IsEmpty) {
                    sb.AppendLine("            _ => value,");
                }
                else {
                    sb.AppendLine("            _ => value with");
                    AppendHiddenMembers(sb, redactor, "            ");
                    sb.AppendLine(",");
                }

                sb.AppendLine("        };");
            }
            else {
                var helper = redactor.Kind switch {
                    RedactorKind.Nullable => "Nullable",
                    RedactorKind.Array => "Array",
                    RedactorKind.List => "List",
                    RedactorKind.ImmutableArray => "ImmutableArray",
                    RedactorKind.Created => "Created",
                    _ => "Sequence"
                };
                sb.AppendLine(
                    $"        return global::Elarion.Pipeline.ResponseRedaction.{helper}(value, __Hide{redactor.Element}, permissions);");
            }

            sb.AppendLine("    }");
        }

        sb.AppendLine("#nullable restore");
    }
}
