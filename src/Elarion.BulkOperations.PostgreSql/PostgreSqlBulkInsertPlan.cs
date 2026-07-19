using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Elarion.EntityFrameworkCore.BulkOperations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

namespace Elarion.BulkOperations.PostgreSql;

/// <summary>
/// The compiled per-entity-type COPY plan: the <c>COPY … FROM STDIN (FORMAT BINARY)</c> command and one
/// compiled writer delegate per column. Built once per (model, CLR type) and cached — the hot loop then
/// runs allocation-light typed writes with the value converter inlined into the compiled expression, no
/// per-value boxing or reflection.
/// </summary>
internal sealed class PostgreSqlBulkInsertPlan<TEntity> where TEntity : class {
    public required string CopyCommand { get; init; }

    public required Func<NpgsqlBinaryImporter, TEntity, CancellationToken, Task>[] ColumnWriters { get; init; }

    public required bool RequiresExactRuntimeType { get; init; }

    /// <summary>The resolved target; the staged (upsert) path reads conflict metadata off it.</summary>
    public required BulkInsertTarget Target { get; init; }

    /// <summary>The delimited (schema-qualified) target table.</summary>
    public required string DelimitedTable { get; init; }

    /// <summary>Delimited column names, aligned with <see cref="ColumnWriters"/>.</summary>
    public required string[] DelimitedColumnNames { get; init; }

    /// <summary>Store types per column (facets included), for staging-table DDL.</summary>
    public required string[] ColumnStoreTypes { get; init; }

    public string DelimitedColumnList => string.Join(", ", DelimitedColumnNames);

    public static PostgreSqlBulkInsertPlan<TEntity> Create(DbContext context) {
        var target = BulkInsertTargetResolver.Resolve(context.Model, typeof(TEntity), IsNpgsqlStoreGenerated);
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var table = sqlHelper.DelimitIdentifier(target.StoreObject.Name, target.StoreObject.Schema);
        var delimitedColumns = target.Columns.Select(c => sqlHelper.DelimitIdentifier(c.ColumnName)).ToArray();

        return new PostgreSqlBulkInsertPlan<TEntity> {
            CopyCommand = $"COPY {table} ({string.Join(", ", delimitedColumns)}) FROM STDIN (FORMAT BINARY)",
            ColumnWriters = [.. target.Columns.Select(BuildColumnWriter)],
            RequiresExactRuntimeType = target.RequiresExactRuntimeType,
            Target = target,
            DelimitedTable = table,
            DelimitedColumnNames = delimitedColumns,
            ColumnStoreTypes = [.. target.Columns.Select(c => c.Property.GetRelationalTypeMapping().StoreType)]
        };
    }

    // Identity/serial columns are store-generated on PostgreSQL and must be absent from the COPY
    // column list (GENERATED ALWAYS even rejects explicit values); the relational-neutral resolver
    // cannot see the Npgsql strategy, so it takes this refinement as a callback.
    private static bool IsNpgsqlStoreGenerated(IProperty property, StoreObjectIdentifier storeObject) {
        return property.GetValueGenerationStrategy(storeObject) is NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
            or NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
            or NpgsqlValueGenerationStrategy.SerialColumn;
    }

    private static Func<NpgsqlBinaryImporter, TEntity, CancellationToken, Task> BuildColumnWriter(
        BulkInsertColumn column) {
        var mapping = column.Property.GetRelationalTypeMapping();
        var (npgsqlDbType, dataTypeName) = ResolveWriteType(mapping);

        var importer = Expression.Parameter(typeof(NpgsqlBinaryImporter), "importer");
        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var cancellationToken = Expression.Parameter(typeof(CancellationToken), "ct");

        var body = column.IsDiscriminator
            ? BuildDiscriminatorWrite(column, mapping, npgsqlDbType, dataTypeName, importer, cancellationToken)
            : BuildMemberWrite(column, mapping, npgsqlDbType, dataTypeName, importer, entity, cancellationToken);

        return Expression
            .Lambda<Func<NpgsqlBinaryImporter, TEntity, CancellationToken, Task>>(body, importer, entity,
                cancellationToken)
            .Compile();
    }

    private static Expression BuildDiscriminatorWrite(
        BulkInsertColumn column,
        RelationalTypeMapping mapping,
        NpgsqlDbType? npgsqlDbType,
        string? dataTypeName,
        ParameterExpression importer,
        ParameterExpression cancellationToken) {
        // The discriminator is a per-entity-type constant, so its provider conversion runs once here.
        var providerValue = mapping.Converter is { } converter
            ? converter.ConvertToProvider(column.DiscriminatorValue)
            : column.DiscriminatorValue;
        if (providerValue is null) return Expression.Call(importer, WriteNullAsyncMethod, cancellationToken);

        var providerType = mapping.Converter?.ProviderClrType ?? mapping.ClrType;
        return BuildWriteCall(importer, Expression.Constant(providerValue, providerType), npgsqlDbType, dataTypeName,
            cancellationToken);
    }

    private static Expression BuildMemberWrite(
        BulkInsertColumn column,
        RelationalTypeMapping mapping,
        NpgsqlDbType? npgsqlDbType,
        string? dataTypeName,
        ParameterExpression importer,
        ParameterExpression entity,
        ParameterExpression cancellationToken) {
        var property = column.Property;

        // Walk the complex-property chain first: a null anywhere along it nulls the column. The null
        // checks are combined with OrElse in chain order, so the value branch (and the leaf null check
        // itself) only evaluates once every step is known non-null.
        var nullChecks = new List<Expression>();
        Expression instance = entity;
        foreach (var complexProperty in column.ComplexPath) {
            instance = Expression.MakeMemberAccess(instance, GetClrMember(complexProperty));
            if (Nullable.GetUnderlyingType(instance.Type) is not null) {
                nullChecks.Add(Expression.Not(Expression.Property(instance, nameof(Nullable<int>.HasValue))));
                instance = Expression.Property(instance, nameof(Nullable<int>.Value));
            }
            else if (!instance.Type.IsValueType) {
                nullChecks.Add(Expression.ReferenceEqual(instance, Expression.Constant(null, instance.Type)));
            }
        }

        var value = Expression.MakeMemberAccess(instance, GetClrMember(property));
        var modelType = value.Type;
        var underlyingModelType = Nullable.GetUnderlyingType(modelType);

        // Split the value into "is it null" and "the non-null model value" so the converter (which EF
        // declares over the non-nullable model type) composes without null-propagation inside it.
        Expression nonNullValue = value;
        if (underlyingModelType is not null) {
            nullChecks.Add(Expression.Not(Expression.Property(value, nameof(Nullable<int>.HasValue))));
            nonNullValue = Expression.Property(value, nameof(Nullable<int>.Value));
        }
        else if (!modelType.IsValueType) {
            nullChecks.Add(Expression.ReferenceEqual(value, Expression.Constant(null, modelType)));
        }

        var isNull = nullChecks.Count == 0 ? null : nullChecks.Aggregate(Expression.OrElse);

        if (mapping.Converter is { } converter) {
            var parameterType = converter.ConvertToProviderExpression.Parameters[0].Type;
            if (nonNullValue.Type != parameterType) nonNullValue = Expression.Convert(nonNullValue, parameterType);
            nonNullValue = Expression.Invoke(converter.ConvertToProviderExpression, nonNullValue);
            if (Nullable.GetUnderlyingType(nonNullValue.Type) is not null)
                // A converter declared over nullable types cannot yield null for the non-null input we
                // feed it (EF composes null handling outside converters), so unwrap for the typed write.
                nonNullValue = Expression.Property(nonNullValue, nameof(Nullable<int>.Value));
        }

        var write = BuildWriteCall(importer, nonNullValue, npgsqlDbType, dataTypeName, cancellationToken);
        return isNull is null
            ? write
            : Expression.Condition(isNull, Expression.Call(importer, WriteNullAsyncMethod, cancellationToken), write);
    }

    private static MemberInfo GetClrMember(IPropertyBase property) {
        return (MemberInfo?)property.PropertyInfo ?? property.FieldInfo
            ?? throw new NotSupportedException(
                $"The property '{property.DeclaringType.DisplayName()}.{property.Name}' has no CLR member to read.");
    }

    private static Expression BuildWriteCall(
        ParameterExpression importer,
        Expression providerValue,
        NpgsqlDbType? npgsqlDbType,
        string? dataTypeName,
        ParameterExpression cancellationToken) {
        return npgsqlDbType is { } dbType
            ? Expression.Call(
                importer,
                WriteAsyncWithDbTypeMethod.MakeGenericMethod(providerValue.Type),
                providerValue,
                Expression.Constant(dbType),
                cancellationToken)
            : Expression.Call(
                importer,
                WriteAsyncWithNameMethod.MakeGenericMethod(providerValue.Type),
                providerValue,
                Expression.Constant(dataTypeName!),
                cancellationToken);
    }

    // Resolve the per-column write addressing once at plan time: prefer the mapping's NpgsqlDbType
    // (exact, no per-write name lookup); mappings that are only name-addressed (mapped enums, some
    // plugin types) fall back to the facet-stripped store type name.
    private static (NpgsqlDbType? NpgsqlDbType, string? DataTypeName) ResolveWriteType(RelationalTypeMapping mapping) {
        if (mapping is Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping.NpgsqlTypeMapping npgsqlMapping)
            try {
                return (npgsqlMapping.NpgsqlDbType, null);
            }
            catch (InvalidOperationException) {
                // Name-addressed mapping without an NpgsqlDbType.
            }

        return (null, StripFacets(mapping.StoreType));
    }

    // "character varying(3)" → "character varying", "numeric(18,2)" → "numeric",
    // "character varying(3)[]" → "character varying[]": binary COPY resolves handlers by base type name.
    private static string StripFacets(string storeType) {
        var open = storeType.IndexOf('(');
        if (open < 0) return storeType;

        var close = storeType.IndexOf(')', open);
        var prefix = storeType[..open].TrimEnd();
        return close < 0 ? prefix : prefix + storeType[(close + 1)..];
    }

    private static readonly MethodInfo WriteNullAsyncMethod =
        typeof(NpgsqlBinaryImporter).GetMethod(nameof(NpgsqlBinaryImporter.WriteNullAsync))!;

    private static readonly MethodInfo WriteAsyncWithDbTypeMethod = GetWriteAsyncMethod(typeof(NpgsqlDbType));

    private static readonly MethodInfo WriteAsyncWithNameMethod = GetWriteAsyncMethod(typeof(string));

    private static MethodInfo GetWriteAsyncMethod(Type secondParameterType) {
        return typeof(NpgsqlBinaryImporter)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m =>
                m.Name == nameof(NpgsqlBinaryImporter.WriteAsync)
                && m.IsGenericMethodDefinition
                && m.GetParameters() is [_, var second, var last]
                && second.ParameterType == secondParameterType
                && last.ParameterType == typeof(CancellationToken));
    }
}

/// <summary>
/// Plan cache keyed by model so recycled models release their plans; within a model, one plan per CLR
/// entity type.
/// </summary>
internal static class PostgreSqlBulkInsertPlanCache {
    private static readonly ConditionalWeakTable<IModel, ConcurrentDictionary<Type, object>> Cache = new();

    public static PostgreSqlBulkInsertPlan<TEntity> Get<TEntity>(DbContext context) where TEntity : class {
        var plans = Cache.GetValue(context.Model, static _ => new ConcurrentDictionary<Type, object>());
        return (PostgreSqlBulkInsertPlan<TEntity>)plans.GetOrAdd(
            typeof(TEntity),
            static (_, ctx) => PostgreSqlBulkInsertPlan<TEntity>.Create(ctx),
            context);
    }
}
