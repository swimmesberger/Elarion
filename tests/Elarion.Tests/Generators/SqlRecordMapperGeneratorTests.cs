using AwesomeAssertions;
using Elarion.Sql.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class SqlRecordMapperGeneratorTests {
    private const string NpgsqlAssemblyAttribute =
        "[assembly: Elarion.Sql.UseElarionSql(Provider = Elarion.Sql.SqlProvider.Npgsql)]";

    private const string OrderSource =
        """
        namespace Sample.Domain {
            public enum OrderStatus { Draft = 0, Open = 1 }

            [Elarion.Sql.SqlRecord("orders")]
            public sealed partial record Order {
                public required System.Guid Id { get; init; }
                public required string CustomerName { get; init; }
                [Elarion.Sql.SqlColumn("note_text")]
                public string? Note { get; init; }
                public int Quantity { get; init; }
                public OrderStatus Status { get; init; }
                public OrderStatus? PreviousStatus { get; init; }
                public System.DateTimeOffset CreatedAt { get; init; }
                [Elarion.Sql.SqlIgnore]
                public string? Transient { get; init; }
                public bool HasNote => Note is not null;
            }
        }
        """;

    [Fact]
    public void SqlRecord_EmitsMapperWithConstantsOrdinalsAndTypedReads() {
        var result = Generate(OrderSource);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Domain_Order.SqlMapper.g.cs");

        source.Should().Contain(
            "public sealed partial class OrderSqlMapper : global::Elarion.Sql.ISqlRowMapper<global::Sample.Domain.Order>");
        source.Should().Contain("public const string TableName = \"orders\";");
        source.Should().Contain("public const string CustomerName = \"customer_name\";");
        source.Should().Contain("public const string Note = \"note_text\";");
        source.Should().Contain(
            "public const string All = \"id, customer_name, note_text, quantity, status, previous_status, created_at\";");
        source.Should().Contain(
            "public const string AllParameters = \"@id, @customer_name, @note_text, @quantity, @status, @previous_status, @created_at\";");
        source.Should().Contain(
            "public const string AllAssignments = \"id = @id, customer_name = @customer_name, note_text = @note_text, quantity = @quantity, status = @status, previous_status = @previous_status, created_at = @created_at\";");
        // Statement constants: the two statements with zero query logic; everything else stays hand-written.
        source.Should().Contain(
            "public const string Insert = \"INSERT INTO orders (id, customer_name, note_text, quantity, status, previous_status, created_at) VALUES (@id, @customer_name, @note_text, @quantity, @status, @previous_status, @created_at)\";");
        source.Should().Contain(
            "public const string Select = \"SELECT id, customer_name, note_text, quantity, status, previous_status, created_at FROM orders\";");
        // Ordinals resolve once per result set; reads are synchronous and typed.
        source.Should().Contain("Id = reader.GetOrdinal(Columns.Id);");
        source.Should().Contain("Id = reader.GetFieldValue<global::System.Guid>(ordinals.Id),");
        source.Should().Contain(
            "Note = reader.IsDBNull(ordinals.Note) ? null : reader.GetFieldValue<string>(ordinals.Note),");
        // Enums read through their underlying type; nullable enums null-check first.
        source.Should()
            .Contain("Status = (global::Sample.Domain.OrderStatus)reader.GetFieldValue<int>(ordinals.Status),");
        source.Should().Contain(
            "PreviousStatus = reader.IsDBNull(ordinals.PreviousStatus) ? default(global::Sample.Domain.OrderStatus?) : (global::Sample.Domain.OrderStatus)reader.GetFieldValue<int>(ordinals.PreviousStatus),");
        // Excluded members appear nowhere.
        source.Should().NotContain("Transient");
        source.Should().NotContain("HasNote");
        // Stateless mapper: static Instance, parameter binding by column name.
        source.Should().Contain("public static OrderSqlMapper Instance { get; } = new OrderSqlMapper();");
        source.Should().Contain("parameter.ParameterName = Columns.Quantity;");
        source.Should().Contain("parameter.Value = (int)row.Status;");
        source.Should().Contain(
            "parameter.Value = row.PreviousStatus is null ? (object)global::System.DBNull.Value : (int)row.PreviousStatus.Value;");
    }

    [Fact]
    public void SqlRecord_PositionalRecord_ConstructsThroughPrimaryConstructor() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed partial record OrderLine(System.Guid Id, string Sku, int Count);
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Domain_OrderLine.SqlMapper.g.cs");

        source.Should().Contain("public const string TableName = \"order_line\";");
        source.Should().Contain("var __Id = reader.GetFieldValue<global::System.Guid>(ordinals.Id);");
        source.Should().Contain("return new global::Sample.Domain.OrderLine(__Id, __Sku, __Count);");
    }

    [Fact]
    public void SqlRecord_JsonColumn_RidesTheCanonicalAccessorViaAmbientInstance() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed record Profile { public int Weight { get; init; } }

                [Elarion.Sql.SqlRecord]
                public sealed partial record Client {
                    public required System.Guid Id { get; init; }
                    [Elarion.Sql.SqlJson]
                    public Profile? Settings { get; init; }
                }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Domain_Client.SqlMapper.g.cs");

        // The DI ctor stays; the static Instance now exists too, reading the ambient accessor lazily.
        source.Should().Contain(
            "public ClientSqlMapper(global::Elarion.Abstractions.Serialization.IElarionJsonSerialization json)");
        source.Should().Contain("public static ClientSqlMapper Instance { get; } = new ClientSqlMapper();");
        source.Should().Contain("_json ?? global::Elarion.Sql.ElarionSqlJson.Serialization");
        source.Should().Contain("Json.GetTypeInfo<global::Sample.Domain.Profile>()");
        source.Should()
            .Contain(
                "global::System.Text.Json.JsonSerializer.Deserialize(reader.GetFieldValue<string>(ordinals.Settings), JsonTypeInfoSettings)");
        // Provider-neutral by default: no Npgsql types.
        source.Should().NotContain("NpgsqlDbType");
    }

    [Fact]
    public void SqlRecord_JsonAssembly_RegistersTheAmbientInstaller() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed record Profile { public int Weight { get; init; } }

                [Elarion.Sql.SqlRecord]
                public sealed partial record Client {
                    public required System.Guid Id { get; init; }
                    [Elarion.Sql.SqlJson]
                    public Profile? Settings { get; init; }
                }
            }
            """);

        NoErrors(result);
        var source = GetGenerated(result, "ElarionSqlMapperRegistration.g.cs");

        source.Should().Contain("AddHostedService<global::Elarion.Sql.ElarionSqlJsonInstaller>");
    }

    [Fact]
    public void SqlRecord_NonJsonAssembly_DoesNotRegisterTheInstaller() {
        var result = Generate(OrderSource);

        NoErrors(result);
        var source = GetGenerated(result, "ElarionSqlMapperRegistration.g.cs");

        source.Should().NotContain("ElarionSqlJsonInstaller");
    }

    [Fact]
    public void SqlRecord_EmitsSelfMappingPartial() {
        var result = Generate(OrderSource);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Domain_Order.SqlRecord.g.cs");

        source.Should().Contain(
            "partial record Order : global::Elarion.Sql.ISqlRecord<Order>");
        source.Should().Contain(
            "public static global::Elarion.Sql.ISqlRowMapper<Order> SqlMapper => global::Sample.Domain.OrderSqlMapper.Instance;");
        source.Should().Contain(
            "public static global::Elarion.Sql.SqlStatement Table { get; } =");
        source.Should().Contain("global::Elarion.Sql.SqlStatement.Verbatim(\"orders\");");
        source.Should().Contain(
            "global::Elarion.Sql.SqlStatement.Verbatim(\"SELECT id, customer_name, note_text, quantity, status, previous_status, created_at FROM orders\");");
    }

    [Fact]
    public void SqlRecord_NonPartial_ReportsElsql010() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed record Row {
                    public required System.Guid Id { get; init; }
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL010");
        // The mapper still emits (the explicit-mapper escape hatch keeps working); only self-mapping is skipped.
        result.GeneratedTrees.Should().Contain(t => t.FilePath.EndsWith("Sample_Domain_Row.SqlMapper.g.cs"));
        result.GeneratedTrees.Should().NotContain(t => t.FilePath.EndsWith("Sample_Domain_Row.SqlRecord.g.cs"));
    }

    [Fact]
    public void SqlRecord_ReservedMemberName_ReportsElsql011() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed partial record Row {
                    public required System.Guid Id { get; init; }
                    public required string Select { get; init; }
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL011");
    }

    [Fact]
    public void SqlRecord_JsonColumn_UnderNpgsqlTrigger_BindsJsonb() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed record Profile { public int Weight { get; init; } }

                [Elarion.Sql.SqlRecord]
                public sealed partial record Client {
                    public required System.Guid Id { get; init; }
                    [Elarion.Sql.SqlJson]
                    public Profile? Settings { get; init; }
                }
            }
            """,
            NpgsqlAssemblyAttribute);

        NoErrors(result);
        var source = GetGenerated(result, "Sample_Domain_Client.SqlMapper.g.cs");

        source.Should().Contain(
            "((global::Npgsql.NpgsqlParameter)parameter).NpgsqlDbType = global::NpgsqlTypes.NpgsqlDbType.Jsonb;");
    }

    [Fact]
    public void SqlRecord_EmitsPerAssemblyRegistration() {
        var result = Generate(OrderSource);

        NoErrors(result);
        var source = GetGenerated(result, "ElarionSqlMapperRegistration.g.cs");

        source.Should().Contain("public static class SqlRecordMapperGeneratorTestsSqlMapperRegistration");
        source.Should()
            .Contain(
                "public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddElarionSqlMappers(");
        source.Should()
            .Contain(
                "TryAddSingleton<global::Elarion.Sql.ISqlRowMapper<global::Sample.Domain.Order>>(services, static _ => global::Sample.Domain.OrderSqlMapper.Instance);");
    }

    [Fact]
    public void SqlRecord_GeneratedSource_Compiles() {
        var result = Generate(OrderSource);

        NoErrors(result);
        var generated = GetGenerated(result, "Sample_Domain_Order.SqlMapper.g.cs");
        var selfMapping = GetGenerated(result, "Sample_Domain_Order.SqlRecord.g.cs");
        var registration = GetGenerated(result, "ElarionSqlMapperRegistration.g.cs");

        CompileErrors(OrderSource, generated, selfMapping, registration).Should().BeEmpty();
    }

    [Fact]
    public void SqlRecord_UnsupportedPropertyType_ReportsElsql001() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed record Child { public int Value { get; init; } }

                [Elarion.Sql.SqlRecord]
                public sealed record Parent {
                    public required System.Guid Id { get; init; }
                    public Child? Child { get; set; }
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL001");
        result.GeneratedTrees.Should().BeEmpty("a broken record emits nothing");
    }

    [Fact]
    public void SqlRecord_RequiredIgnoredProperty_ReportsElsql003() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed record Row {
                    public required System.Guid Id { get; init; }
                    [Elarion.Sql.SqlIgnore]
                    public required string Name { get; init; }
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL003");
    }

    [Fact]
    public void SqlRecord_NoColumns_ReportsElsql004() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed record Empty {
                    public string Derived => "x";
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL004");
    }

    [Fact]
    public void SqlRecord_NestedOrGenericOrAbstract_ReportsElsql005() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public static class Outer {
                    [Elarion.Sql.SqlRecord]
                    public sealed record Nested { public int Value { get; init; } }
                }

                [Elarion.Sql.SqlRecord]
                public sealed record Generic<T> { public int Value { get; init; } }

                [Elarion.Sql.SqlRecord]
                public abstract record Base { public int Value { get; init; } }
            }
            """);

        result.Diagnostics.Where(d => d.Id == "ELSQL005").Should().HaveCount(3);
    }

    [Fact]
    public void SqlRecord_NoUsableConstructor_ReportsElsql006() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed class Row {
                    public Row(string somethingElse) { }
                    public System.Guid Id { get; set; }
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL006");
    }

    [Fact]
    public void SqlRecord_AnnotatedGetOnlyProperty_ReportsElsql007() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed record Row {
                    public required System.Guid Id { get; init; }
                    [Elarion.Sql.SqlColumn("computed")]
                    public string Computed => "x";
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL007");
    }

    [Fact]
    public void SqlRecord_DuplicateColumnNames_ReportsElsql002() {
        var result = Generate(
            """
            namespace Sample.Domain {
                [Elarion.Sql.SqlRecord]
                public sealed record Row {
                    public required System.Guid Id { get; init; }
                    [Elarion.Sql.SqlColumn("id")]
                    public string? Other { get; init; }
                }
            }
            """);

        result.Diagnostics.Should().ContainSingle(d => d.Id == "ELSQL002");
    }

    [Fact]
    public void IrrelevantEditReusesTrackedOutputs() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new SqlRecordMapperGenerator(), OrderSource, "SqlRecords", "SqlMapperRegistration");
    }

    [Fact]
    public void UnrelatedFileEditDoesNotRerunDiscovery() {
        GeneratorCacheAssert.ReusesDiscoveryAfterUnrelatedFileEdit(
            new SqlRecordMapperGenerator(), OrderSource, "SqlRecords");
    }

    private static GeneratorDriverRunResult Generate(string source, string? assemblyAttribute = null) {
        var trees = new List<SyntaxTree> {
            CSharpSyntaxTree.ParseText(source, ParseOptions)
        };
        if (assemblyAttribute is not null) trees.Add(CSharpSyntaxTree.ParseText(assemblyAttribute, ParseOptions));

        var compilation = CSharpCompilation.Create(
            "SqlRecordMapperGeneratorTests",
            trees,
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new SqlRecordMapperGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static void NoErrors(GeneratorDriverRunResult result) {
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(params string[] sources) {
        var compilation = CSharpCompilation.Create(
            "SqlRecordMapperGeneratedCompile",
            [.. sources.Select(source => CSharpSyntaxTree.ParseText(source, ParseOptions))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return [
            .. trustedPlatformAssemblies!
                .Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(Sql.SqlRecordAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Elarion.Abstractions.Serialization.IElarionJsonSerialization)
                .Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection)
                .Assembly.Location),
            MetadataReference.CreateFromFile(
                typeof(Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions)
                    .Assembly.Location)
        ];
    }
}
