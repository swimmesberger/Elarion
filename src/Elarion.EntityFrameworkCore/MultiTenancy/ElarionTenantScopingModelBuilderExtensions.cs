using System.Linq.Expressions;
using System.Reflection;
using Elarion.Abstractions.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace Elarion.EntityFrameworkCore.MultiTenancy;

/// <summary>
/// Attaches the read leg of ambient tenant scoping: a named query filter on every mapped entity implementing
/// <see cref="ITenantScoped{TTenantId}"/>, so a tenant-scoped table cannot be read across tenants even by a
/// query that says nothing about tenancy.
/// </summary>
/// <remarks>
/// A model-level query filter is normally the wrong tool — it makes a query stop saying what SQL runs, and it
/// does not reach raw SQL or the bulk paths. It earns its keep here for the one reason it usually does not: the
/// predicate is a <b>security boundary</b> that has to hold even when a developer forgets, which is exactly the
/// multi-tenant case. Per-screen visibility (archived, draft, hidden) stays an explicit predicate at the call
/// site.
/// </remarks>
public static class ElarionTenantScopingModelBuilderExtensions {
    private static readonly MethodInfo CurrentTenantIdMethod =
        typeof(ElarionTenantScoping).GetMethod(nameof(ElarionTenantScoping.CurrentTenantId))!;

    private static readonly MethodInfo IsSystemScopeMethod =
        typeof(ElarionTenantScoping).GetMethod(nameof(ElarionTenantScoping.IsSystemScope))!;

    /// <summary>
    /// Applies the tenant query filter to every tenant-scoped entity in the model, and records which property
    /// carries the tenant id so the stamping interceptor needs no reflection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this <b>last</b> in <c>OnModelCreating</c> (the generated <c>ConfigureEntities</c> already does), so
    /// it also covers entities other configuration seams added and children discovered through navigations.
    /// Owned types and derived types in a hierarchy are skipped: Entity Framework Core defines a filter on the
    /// root entity type, and it applies to the whole hierarchy from there.
    /// </para>
    /// <para>
    /// The filter is <see cref="ElarionTenantScoping.QueryFilterKey">named</see>, so an application's own
    /// filters on the same entity survive alongside it and a query can drop just this one.
    /// </para>
    /// </remarks>
    /// <param name="modelBuilder">The model being built.</param>
    /// <param name="context">
    /// The context being configured. Its <i>type</i> is what matters: Entity Framework Core rewrites the
    /// reference to point at whichever instance is executing the query, so the filter reads the current scope's
    /// tenant rather than the one that happened to build the model.
    /// </param>
    /// <returns>The model builder for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// An entity implements <see cref="ITenantScoped{TTenantId}"/> with an unsupported key type, more than
    /// once, or with an unmapped tenant property — each of which would leave the entity silently unfiltered.
    /// </exception>
    public static ModelBuilder ApplyElarionTenantScoping(this ModelBuilder modelBuilder, DbContext context) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);

        // The context is referenced by type; EF substitutes the executing instance into the filter per query.
        var contextReference = Expression.Constant(context, context.GetType());

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList()) {
            if (entityType.BaseType is not null || entityType.IsOwned()) continue;

            var tenantProperty = FindTenantProperty(entityType.ClrType);
            if (tenantProperty is null) continue;

            var mapped = entityType.FindProperty(tenantProperty.Name)
                         ?? throw new InvalidOperationException(
                             $"Entity '{entityType.ClrType.Name}' is tenant-scoped, but its tenant property "
                             + $"'{tenantProperty.Name}' is not mapped. An unmapped tenant key can be neither "
                             + "filtered nor stamped; map it, or drop ITenantScoped from the entity.");

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(
                    ElarionTenantScoping.QueryFilterKey,
                    BuildFilter(entityType.ClrType, tenantProperty, contextReference));

            // The write leg reads the property name off the model rather than rediscovering the interface.
            entityType.SetAnnotation(ElarionTenantScoping.TenantPropertyAnnotation, mapped.Name);
        }

        return modelBuilder;
    }

    /// <summary>
    /// The property implementing <c>ITenantScoped&lt;TTenantId&gt;.TenantId</c> on <paramref name="clrType"/>,
    /// or <see langword="null"/> when the type is not tenant-scoped.
    /// </summary>
    private static PropertyInfo? FindTenantProperty(Type clrType) {
        Type? closed = null;
        foreach (var candidate in clrType.GetInterfaces()) {
            if (!candidate.IsGenericType ||
                candidate.GetGenericTypeDefinition() != typeof(ITenantScoped<>)) continue;

            if (closed is not null)
                throw new InvalidOperationException(
                    $"Entity '{clrType.Name}' implements ITenantScoped<> more than once; a row belongs to "
                    + "exactly one tenant, so exactly one tenant key is meaningful.");

            closed = candidate;
        }

        if (closed is null) return null;

        var keyType = closed.GetGenericArguments()[0];
        if (keyType != typeof(Guid) && keyType != typeof(string) && keyType != typeof(int) &&
            keyType != typeof(long))
            throw new InvalidOperationException(
                $"Entity '{clrType.Name}' is tenant-scoped on key type '{keyType.Name}', which is not "
                + "supported. Use Guid, string, int, or long — the key types a tenant rule can compare in the "
                + "database.");

        var map = clrType.GetInterfaceMap(closed);
        for (var i = 0; i < map.InterfaceMethods.Length; i++) {
            if (!map.InterfaceMethods[i].Name.EndsWith("get_TenantId", StringComparison.Ordinal)) continue;

            var getter = map.TargetMethods[i];
            foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.GetGetMethod(true) == getter)
                    return property;
        }

        return null;
    }

    /// <summary>
    /// Builds <c>e =&gt; IsSystemScope(ctx) || e.TenantId == As{Key}(CurrentTenantId(ctx))</c>.
    /// </summary>
    /// <remarks>
    /// The conversion yields a <i>nullable</i> key, so an unresolved tenant compares against SQL <c>NULL</c> and
    /// matches nothing: the filter fails closed instead of falling back to the default key value, which would
    /// quietly expose every row whose tenant column happens to be <c>Guid.Empty</c> or <c>0</c>.
    /// </remarks>
    private static LambdaExpression BuildFilter(
        Type clrType,
        PropertyInfo tenantProperty,
        ConstantExpression contextReference) {
        var entity = Expression.Parameter(clrType, "__e");

        var expected = Expression.Call(
            ConverterFor(tenantProperty.PropertyType),
            Expression.Call(CurrentTenantIdMethod, contextReference));

        Expression actual = Expression.Property(entity, tenantProperty);
        if (actual.Type != expected.Type) actual = Expression.Convert(actual, expected.Type);

        var body = Expression.OrElse(
            Expression.Call(IsSystemScopeMethod, contextReference),
            Expression.Equal(actual, expected));

        return Expression.Lambda(body, entity);
    }

    private static MethodInfo ConverterFor(Type keyType) {
        var name = keyType == typeof(Guid) ? nameof(ElarionTenantScoping.AsGuid)
            : keyType == typeof(string) ? nameof(ElarionTenantScoping.AsString)
            : keyType == typeof(int) ? nameof(ElarionTenantScoping.AsInt32)
            : nameof(ElarionTenantScoping.AsInt64);

        return typeof(ElarionTenantScoping).GetMethod(name)!;
    }
}
