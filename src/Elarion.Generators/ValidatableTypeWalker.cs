using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// The single "is this request validatable" computation shared by <see cref="HandlerRegistrationGenerator"/>
/// (which auto-attaches the framework <c>ValidationDecorator</c>) and <see cref="ValidationResolverGenerator"/>
/// (which emits the per-module <c>IValidatableInfoResolver</c>), so the decorator and the resolver can never
/// disagree about which requests carry validation metadata (ADR-0027).
/// <para>
/// Starting from a request type, the walker traverses public instance properties transitively — unwrapping
/// <c>Nullable&lt;T&gt;</c>, arrays, and <c>IEnumerable&lt;T&gt;</c> element types, skipping primitives,
/// <c>string</c>, enums, and leaf BCL value types; cycle-safe. A type is <em>annotated</em> when any of its
/// properties (or, for records, the matching primary-constructor parameter) carries an attribute deriving from
/// <c>System.ComponentModel.DataAnnotations.ValidationAttribute</c>, when the type itself carries one, or when
/// it implements <c>IValidatableObject</c>. A request is <em>validatable</em> when its graph contains any
/// annotated type. The resolver must register an entry for every annotated <em>or recursion-carrying</em> type
/// in the graph, because <c>Microsoft.Extensions.Validation</c> recurses into a nested value only when the
/// value's type resolves through <c>ValidationOptions.TryGetValidatableTypeInfo</c>.
/// </para>
/// <para>
/// The walker runs inside a compilation-combined pipeline stage; symbols never enter pipeline values. Attribute
/// instances are rendered to constant-construction C# expressions (typed literals from compile-time
/// <see cref="AttributeData"/>) so generated <c>GetValidationAttributes()</c> implementations return cached,
/// reflection-free arrays.
/// </para>
/// </summary>
internal static class ValidatableTypeWalker {
    private const string ValidatableObjectDisplay = "System.ComponentModel.DataAnnotations.IValidatableObject";
    private const string DisplayAttributeDisplay = "System.ComponentModel.DataAnnotations.DisplayAttribute";

    // .NET 11 asynchronous validation. Absent from older reference sets, so nothing matches there.
    // C# 15 unions (.NET 11). Recognized by IUnion — what every compiler-declared union implements (its [Union]
    // attribute is synthesized at emit, so a source declaration does not show it) and what the runtime walker unwraps.
    // A hand-written union that only carries [Union] is walked like any other struct.
    private const string UnionInterfaceDisplay = "System.Runtime.CompilerServices.IUnion";

    private const string AsyncValidationAttributeDisplay =
        "System.ComponentModel.DataAnnotations.AsyncValidationAttribute";

    private const string AsyncValidatableObjectDisplay =
        "System.ComponentModel.DataAnnotations.IAsyncValidatableObject";

    private static readonly SymbolDisplayFormat Fmt = SymbolDisplayFormat.FullyQualifiedFormat;

    // Leaf BCL types never walked (beyond what SpecialType already excludes: primitives, string, object,
    // decimal, DateTime, enums, delegates). These have public properties but carry no user annotations.
    private static readonly HashSet<string> ExcludedLeafTypes = new(StringComparer.Ordinal) {
        "System.Guid",
        "System.DateTimeOffset",
        "System.DateOnly",
        "System.TimeOnly",
        "System.TimeSpan",
        "System.Uri",
        "System.Version",
        "System.Type"
    };

    /// <summary>
    /// Per-resolution-pass walk state: memoizes visited type nodes and per-root validatability across all
    /// handlers of one compilation pass. Never stored in a pipeline value (it holds symbols).
    /// </summary>
    internal sealed class Context(IAssemblySymbol currentAssembly) {
        public IAssemblySymbol CurrentAssembly { get; } = currentAssembly;

        internal Dictionary<ITypeSymbol, TypeNode> Nodes { get; } =
            new(SymbolEqualityComparer.Default);

        internal Dictionary<ITypeSymbol, bool> ValidatableCache { get; } =
            new(SymbolEqualityComparer.Default);
    }

    /// <summary>A visited graph node. Transient within one resolution pass — holds symbols, never cached in the pipeline.</summary>
    internal sealed class TypeNode(ITypeSymbol type) {
        public ITypeSymbol Type { get; } = type;

        /// <summary>Direct annotation: a validation attribute on the type or a property, or IValidatableObject.</summary>
        public bool Annotated { get; set; }

        public List<string> TypeAttributes { get; } = new();

        /// <summary>
        /// Human-readable descriptions of the type's asynchronous validators (an <c>AsyncValidationAttribute</c>
        /// on the type or a property, or <c>IAsyncValidatableObject</c>) — each runs before the transaction.
        /// </summary>
        public List<string> AsyncValidators { get; } = new();

        public List<PropertyEntry> Properties { get; } = new();
    }

    internal sealed record PropertyEntry(
        string Name,
        string DisplayName,
        string PropertyTypeFqn,
        EquatableArray<string> Attributes,
        IReadOnlyList<TypeNode> Targets);

    /// <summary>
    /// True when the request type's graph contains any annotated type — i.e. the handler needs the
    /// <c>ValidationDecorator</c> and the resolver an entry for the request. Memoized per root in the context.
    /// </summary>
    public static bool IsValidatable(ITypeSymbol requestType, Context context) {
        if (context.ValidatableCache.TryGetValue(requestType, out var cached))
            return cached;

        var result = false;
        if (IsWalkable(requestType, context)) {
            var root = Visit(requestType, context);
            result = ReachesAnnotated(root, new HashSet<TypeNode>());
        }

        context.ValidatableCache[requestType] = result;
        return result;
    }

    /// <summary>
    /// Builds the value-equatable emission models for every type that must be registered with a resolver
    /// serving <paramref name="roots"/>: each type in the roots' graphs that is annotated or that
    /// (transitively) contains an annotated type. Deduped within the set and ordered by fully-qualified name,
    /// so emission is deterministic. A member is included when it carries attributes or when its (unwrapped)
    /// type is itself registered — the recursion carrier the runtime walker follows.
    /// </summary>
    public static EquatableArray<ValidatableTypeModel> BuildModels(IReadOnlyList<ITypeSymbol> roots, Context context) {
        // The reachable closure over all roots.
        var reachable = new HashSet<TypeNode>();
        foreach (var root in roots) {
            if (!IsWalkable(root, context))
                continue;

            CollectClosure(Visit(root, context), reachable);
        }

        if (reachable.Count == 0)
            return EquatableArray<ValidatableTypeModel>.Empty;

        // Fixpoint: registered = annotated ∪ { t | t has a member whose target is registered }.
        var registered = new HashSet<TypeNode>();
        foreach (var node in reachable)
            if (node.Annotated)
                registered.Add(node);

        var changed = registered.Count > 0;
        while (changed) {
            changed = false;
            foreach (var node in reachable) {
                if (registered.Contains(node))
                    continue;

                foreach (var property in node.Properties)
                    if (property.Targets.Any(registered.Contains)) {
                        registered.Add(node);
                        changed = true;
                        break;
                    }
            }
        }

        if (registered.Count == 0)
            return EquatableArray<ValidatableTypeModel>.Empty;

        var models = ImmutableArray.CreateBuilder<ValidatableTypeModel>(registered.Count);
        foreach (var node in registered) {
            var members = ImmutableArray.CreateBuilder<ValidatablePropertyModel>();
            foreach (var property in node.Properties) {
                var carriesRecursion = property.Targets.Any(registered.Contains);
                if (property.Attributes.IsEmpty && !carriesRecursion)
                    continue;

                members.Add(new ValidatablePropertyModel(
                    property.PropertyTypeFqn,
                    property.Name,
                    property.DisplayName,
                    property.Attributes));
            }

            models.Add(new ValidatableTypeModel(
                node.Type.ToDisplayString(Fmt),
                node.TypeAttributes.ToImmutableArray(),
                members.ToImmutable()));
        }

        models.Sort(static (left, right) => string.CompareOrdinal(left.TypeFqn, right.TypeFqn));
        return models.ToImmutable();
    }

    /// <summary>
    /// Describes every asynchronous validator in the request type's graph (sorted, distinct): .NET 11's
    /// <c>AsyncValidationAttribute</c> and <c>IAsyncValidatableObject</c> run in the validation decorator, before the
    /// handler's transaction, which makes a database or service lookup there a time-of-check/time-of-use race.
    /// </summary>
    public static EquatableArray<string> FindAsyncValidators(ITypeSymbol requestType, Context context) {
        if (!IsWalkable(requestType, context))
            return EquatableArray<string>.Empty;

        var closure = new HashSet<TypeNode>();
        CollectClosure(Visit(requestType, context), closure);

        var descriptions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in closure)
            descriptions.UnionWith(node.AsyncValidators);

        return descriptions.ToImmutableArray();
    }

    private static void CollectClosure(TypeNode node, HashSet<TypeNode> closure) {
        if (!closure.Add(node))
            return;

        foreach (var property in node.Properties)
            foreach (var target in property.Targets)
                CollectClosure(target, closure);
    }

    private static bool ReachesAnnotated(TypeNode node, HashSet<TypeNode> visited) {
        if (!visited.Add(node))
            return false;

        if (node.Annotated)
            return true;

        foreach (var property in node.Properties)
            foreach (var target in property.Targets)
                if (ReachesAnnotated(target, visited))
                    return true;

        return false;
    }

    private static TypeNode Visit(ITypeSymbol type, Context context) {
        if (context.Nodes.TryGetValue(type, out var existing))
            return existing;

        var node = new TypeNode(type);
        // Pre-register before walking properties so a cyclic graph terminates; Annotated is intrinsic to the
        // node and only read after the full build, so the in-cycle early return is safe.
        context.Nodes[type] = node;

        // The node's symbol may carry the referencing property's nullable annotation; describe the type itself.
        var typeDisplay = type.WithNullableAnnotation(NullableAnnotation.None)
            .ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var typeAttributeData = type.GetAttributes();
        node.TypeAttributes.AddRange(RenderValidationAttributes(typeAttributeData, context));
        var annotated = node.TypeAttributes.Count > 0 ||
                        HandlerShape.Implements(type, ValidatableObjectDisplay);

        AddAsyncAttributes(node, typeAttributeData, typeDisplay);
        if (HandlerShape.Implements(type, AsyncValidatableObjectDisplay))
            node.AsyncValidators.Add($"{typeDisplay} implements IAsyncValidatableObject");

        foreach (var property in CollectProperties(type)) {
            var attributeData = CollectPropertyAttributeData(property);
            var attributes = RenderValidationAttributes(attributeData, context).ToImmutableArray();
            if (!attributes.IsEmpty)
                annotated = true;

            AddAsyncAttributes(node, attributeData, $"{typeDisplay}.{property.Name}");

            var targets = ResolveTargets(property.Type, context);

            node.Properties.Add(new PropertyEntry(
                property.Name,
                ResolveDisplayName(attributeData, property.Name),
                property.Type.ToDisplayString(Fmt),
                attributes,
                targets));
        }

        node.Annotated = annotated;
        return node;
    }

    private static void AddAsyncAttributes(TypeNode node, IEnumerable<AttributeData> attributes, string target) {
        foreach (var attribute in attributes)
            if (DerivesFrom(attribute.AttributeClass, AsyncValidationAttributeDisplay))
                node.AsyncValidators.Add($"[{attribute.AttributeClass!.Name}] on {target}");
    }

    private static bool DerivesFrom(INamedTypeSymbol? type, string baseDisplay) {
        for (var current = type?.BaseType; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == baseDisplay)
                return true;

        return false;
    }

    // Public, gettable instance properties (no indexers), including inherited ones with most-derived-wins
    // dedupe — mirroring the runtime walker, which reads members via DeclaringType.GetProperty(name) on the
    // registered (concrete) type.
    private static List<IPropertySymbol> CollectProperties(ITypeSymbol type) {
        var result = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            foreach (var member in current.GetMembers()) {
                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, GetMethod: not null } property)
                    continue;

                if (property.DeclaredAccessibility != Accessibility.Public || property.IsImplicitlyDeclared)
                    continue;

                if (seen.Add(property.Name))
                    result.Add(property);
            }

        return result;
    }

    // The property's own attributes plus — matching Microsoft's record handling — the attributes of the first
    // constructor parameter with the same name (a positional record's `[StringLength(…)] string Name` lands on
    // the parameter, not the synthesized property).
    private static List<AttributeData> CollectPropertyAttributeData(IPropertySymbol property) {
        var result = new List<AttributeData>(property.GetAttributes());
        if (property.ContainingType is not { } declaringType)
            return result;

        foreach (var constructor in declaringType.InstanceConstructors)
        foreach (var parameter in constructor.Parameters) {
            if (!string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            result.AddRange(parameter.GetAttributes());
            return result;
        }

        return result;
    }

    private static string ResolveDisplayName(List<AttributeData> attributes, string propertyName) {
        foreach (var attribute in attributes) {
            if (attribute.AttributeClass?.ToDisplayString() != DisplayAttributeDisplay)
                continue;

            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Name" && named.Value.Value is string name && name.Length > 0)
                    return name;
        }

        return propertyName;
    }

    /// <summary>
    /// The graph nodes a property recurses into: the unwrapped property type, or — for a C# union, which serializes as
    /// its active case and is validated as that case at the property's own path — every case type. The runtime walker
    /// unwraps <c>IUnion.Value</c> to reach the case, so the union struct itself never needs a resolver entry.
    /// </summary>
    private static IReadOnlyList<TypeNode> ResolveTargets(ITypeSymbol propertyType, Context context) {
        var target = UnwrapWalkTarget(propertyType, context);
        if (target is null)
            return Array.Empty<TypeNode>();

        if (!IsUnion(target))
            return new[] { Visit(target, context) };

        var nodes = new List<TypeNode>();
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { target };
        CollectUnionCaseTargets((INamedTypeSymbol)target, context, nodes, seen);
        return nodes;
    }

    private static void CollectUnionCaseTargets(INamedTypeSymbol union, Context context, List<TypeNode> nodes,
        HashSet<ITypeSymbol> seen) {
        // Case types are the single-parameter public constructors (the basic union pattern).
        foreach (var constructor in union.InstanceConstructors) {
            if (constructor.DeclaredAccessibility != Accessibility.Public || constructor.Parameters.Length != 1)
                continue;

            var caseType = UnwrapWalkTarget(constructor.Parameters[0].Type, context);
            if (caseType is null || !seen.Add(caseType))
                continue;

            if (IsUnion(caseType)) {
                CollectUnionCaseTargets((INamedTypeSymbol)caseType, context, nodes, seen);
                continue;
            }

            var node = Visit(caseType, context);
            if (!nodes.Contains(node))
                nodes.Add(node);
        }
    }

    private static bool IsUnion(ITypeSymbol type) {
        return type is INamedTypeSymbol named && HandlerShape.Implements(named, UnionInterfaceDisplay);
    }

    /// <summary>
    /// Resolves the type a property recurses into: unwraps <c>Nullable&lt;T&gt;</c>, arrays, and
    /// <c>IEnumerable&lt;T&gt;</c> element types (innermost element for nested collections, matching the
    /// runtime walker's per-item resolution), then returns it when it is walkable, else <see langword="null"/>.
    /// </summary>
    private static ITypeSymbol? UnwrapWalkTarget(ITypeSymbol type, Context context) {
        var current = type;
        // A self-referential enumerable (class X : IEnumerable<X>) would unwrap forever; the seen-set breaks it.
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        while (seen.Add(current)) {
            if (current is IArrayTypeSymbol array) {
                current = array.ElementType;
                continue;
            }

            if (current.SpecialType == SpecialType.System_String)
                break;

            if (current is INamedTypeSymbol {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                } nullable) {
                current = nullable.TypeArguments[0];
                continue;
            }

            var element = GetEnumerableElement(current);
            if (element is not null) {
                current = element;
                continue;
            }

            break;
        }

        return IsWalkable(current, context) ? current : null;
    }

    private static ITypeSymbol? GetEnumerableElement(ITypeSymbol type) {
        if (type is INamedTypeSymbol {
                OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T
            } direct)
            return direct.TypeArguments[0];

        foreach (var iface in type.AllInterfaces)
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return iface.TypeArguments[0];

        return null;
    }

    private static bool IsWalkable(ITypeSymbol type, Context context) {
        if (type is not INamedTypeSymbol named)
            return false;

        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return false;

        // Covers primitives, string, object, decimal, DateTime, Nullable (unwrapped before this check), …
        if (named.SpecialType != SpecialType.None)
            return false;

        if (named.IsAnonymousType || named.IsUnboundGenericType)
            return false;

        if (ExcludedLeafTypes.Contains(named.ContainingNamespace?.ToDisplayString() + "." + named.Name))
            return false;

        // A typeof() over the type must compile in the generated resolver (same assembly as the handlers).
        return IsAccessibleFromGeneratedCode(named, context.CurrentAssembly);
    }

    // --- Attribute rendering: compile-time AttributeData -> constant-construction C# expression -------------

    /// <summary>
    /// Renders every attribute deriving from <c>ValidationAttribute</c> as a constant-construction expression
    /// (<c>new StringLengthAttribute(100) { MinimumLength = 3 }</c>) through the shared
    /// <see cref="AttributeArgumentRendering"/> round-tripping. Attributes whose arguments cannot be
    /// represented as compile-time constants, or whose type is not accessible from the generated code, are
    /// skipped (attribute arguments are constants by construction, so this is a guard, not a policy).
    /// </summary>
    private static List<string> RenderValidationAttributes(IEnumerable<AttributeData> attributes, Context context) {
        var result = new List<string>();
        foreach (var attribute in attributes) {
            if (attribute.AttributeClass is not { TypeKind: not TypeKind.Error } attributeClass)
                continue;

            if (!AttributeArgumentRendering.DerivesFromValidationAttribute(attributeClass))
                continue;

            if (TryRenderAttribute(attribute, attributeClass, context, out var rendered))
                result.Add(rendered);
        }

        return result;
    }

    private static bool TryRenderAttribute(
        AttributeData attribute,
        INamedTypeSymbol attributeClass,
        Context context,
        out string rendered) {
        rendered = string.Empty;
        if (!IsAccessibleFromGeneratedCode(attributeClass, context.CurrentAssembly))
            return false;

        var arguments = new List<string>(attribute.ConstructorArguments.Length);
        foreach (var constant in attribute.ConstructorArguments) {
            if (!TryRenderTypedConstant(constant, context, out var value))
                return false;

            arguments.Add(value);
        }

        var initializers = new List<string>(attribute.NamedArguments.Length);
        foreach (var named in attribute.NamedArguments) {
            if (!TryRenderTypedConstant(named.Value, context, out var value))
                return false;

            initializers.Add($"{named.Key} = {value}");
        }

        var expression = $"new {attributeClass.ToDisplayString(Fmt)}({string.Join(", ", arguments)})";
        if (initializers.Count > 0)
            expression += $" {{ {string.Join(", ", initializers)} }}";

        rendered = expression;
        return true;
    }

    private static bool TryRenderTypedConstant(TypedConstant constant, Context context, out string rendered) {
        return AttributeArgumentRendering.TryRenderTypedConstant(
            constant, context.CurrentAssembly, Fmt, attributeArgument: false, out rendered);
    }

    private static bool IsAccessibleFromGeneratedCode(ITypeSymbol type, IAssemblySymbol currentAssembly) {
        return AttributeArgumentRendering.IsAccessibleFromGeneratedCode(type, currentAssembly);
    }
}

/// <summary>
/// The value-equatable emission model for one resolver entry: a type registered with the generated
/// <c>IValidatableInfoResolver</c>, its type-level constant-constructed validation attributes, and its members.
/// </summary>
internal sealed record ValidatableTypeModel(
    string TypeFqn,
    EquatableArray<string> TypeAttributes,
    EquatableArray<ValidatablePropertyModel> Members);

/// <summary>
/// A member of a registered validatable type: emitted when it carries validation attributes or when its
/// (unwrapped) type is itself registered, so the runtime walker recurses through it.
/// </summary>
internal sealed record ValidatablePropertyModel(
    string PropertyTypeFqn,
    string Name,
    string DisplayName,
    EquatableArray<string> Attributes);
