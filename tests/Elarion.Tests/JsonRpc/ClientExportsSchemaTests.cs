using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Modules;
using Elarion.JsonRpc;
using Elarion.Paging;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// The values the schema exports next to the method schemas (ADR-0100): declared upload limits, module constants,
/// named request presets and a handler's sort keys — and nothing at all for a host that declares none.
/// </summary>
public sealed class ClientExportsSchemaTests {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public enum InvoiceStatus { Draft, Approved }

    public sealed record Invoice(DateTime CreatedAt, string Number);

    public sealed record ListInvoicesQuery {
        public bool? ToTransfer { get; init; }
        public InvoiceStatus? Status { get; init; }
        public bool? Exported { get; init; }
        public int Size { get; init; } = 20;
        public string? Scope { get; init; } = "mine";
        public string? Sort { get; init; }
    }

    public sealed record Row(string Number);

    /// <summary>A handler's declarations, as the export reads them from its type.</summary>
    public sealed class ListInvoices {
        [RequestPreset("toTransfer")]
        public static readonly ListInvoicesQuery ToTransfer = new() { ToTransfer = true };

        [RequestPreset("notExported")]
        public static readonly ListInvoicesQuery NotExported =
            new() { Status = InvoiceStatus.Approved, Exported = false, Size = 50 };

        // Clears a member whose default is not null: the client must send the null.
        [RequestPreset("everyone")]
        internal static readonly ListInvoicesQuery Everyone = new() { Scope = null };

        private static readonly SortMap<Invoice> SortKeys = SortMap<Invoice>
            .CreateBuilder("createdAt", i => i.CreatedAt, SortDirection.Descending)
            .Add("number", i => i.Number)
            .Build();
    }

    public sealed record AccountQuery {
        public required string Account { get; init; }
        public required int Year { get; init; }
        public int Size { get; init; } = 20;
    }

    public sealed class ListByAccount {
        // Required members are always sent, even when they hold the CLR default.
        [RequestPreset("main")]
        public static readonly AccountQuery Main = new() { Account = "main", Year = 0 };

        private static readonly SortMap<Invoice> ByDate = SortMap<Invoice>.CreateBuilder("createdAt", i => i.CreatedAt).Build();
        private static readonly SortMap<Invoice> ByNumber = SortMap<Invoice>.CreateBuilder("number", i => i.Number).Build();
    }

    public sealed record ImportCommand(
        [property: UploadLimits(MaxBytes = 1024, ContentTypes = ["text/csv", "application/xml"])] UploadedFile Statement,
        [UploadLimits(ContentTypes = ["application/pdf"])] UploadedFile? Signature) {
        [System.ComponentModel.DataAnnotations.MaxLength(10), UploadLimits(MaxBytes = 2048)]
        public IReadOnlyList<UploadedFile> Receipts { get; init; } = [];

        public UploadedFile? Plain { get; init; }

        [UploadLimits(FileExtensions = [".xlsx", ".xls"])]
        public IReadOnlyList<UploadedFile> Workbooks { get; init; } = [];
    }

    private static JsonRpcDispatcher Dispatcher() {
        return new JsonRpcDispatcher(Options)
            .MapDelegate<ListInvoicesQuery, Row>(
                "invoices.list", (_, _, _) => ValueTask.FromResult<Result<Row>>(new Row("1")))
            .MapDelegate<AccountQuery, Row>(
                "invoices.byAccount", (_, _, _) => ValueTask.FromResult<Result<Row>>(new Row("1")))
            .MapDelegate<ImportCommand, Row>(
                "statements.import", (_, _, _) => ValueTask.FromResult<Result<Row>>(new Row("1")))
            .Freeze();
    }

    private static JsonRpcSchemaExportOptions ExportOptions() {
        return new JsonRpcSchemaExportOptions {
            HandlerTypes = new Dictionary<Type, Type> {
                [typeof(ListInvoicesQuery)] = typeof(ListInvoices),
                [typeof(AccountQuery)] = typeof(ListByAccount)
            },
            ClientConstants = [
                new ModuleClientConstants {
                    Module = "Billing",
                    Constants = [
                        new ClientConstant { Name = "maxReceipts", Type = typeof(int), GetValue = static () => 10 },
                        new ClientConstant {
                            Name = "currencies", Type = typeof(IReadOnlyList<string>),
                            GetValue = static () => new[] { "EUR", "CHF" }
                        },
                        new ClientConstant {
                            Name = "defaultStatus", Type = typeof(InvoiceStatus),
                            GetValue = static () => InvoiceStatus.Approved
                        },
                        new ClientConstant { Name = "maxAmount", Type = typeof(decimal), GetValue = static () => 999_999.99m }
                    ]
                },
                new ModuleClientConstants {
                    Module = "Archive",
                    Constants = [new ClientConstant { Name = "enabled", Type = typeof(bool), GetValue = static () => true }]
                }
            ]
        };
    }

    private static JsonNode Export(JsonRpcSchemaExportOptions? exportOptions = null) {
        return JsonNode.Parse(JsonRpcSchemaExporter.Generate(Dispatcher(), Options, exportOptions ?? ExportOptions()))!;
    }

    [Fact]
    public void Presets_CarryTheMembersThatDifferFromTheDefaults_InTheCanonicalForm() {
        var presets = Export()["methods"]!["invoices.list"]!["presets"]!.AsObject();

        presets.Select(static p => p.Key).Should().Equal("everyone", "notExported", "toTransfer");
        JsonNode.DeepEquals(presets["toTransfer"], JsonNode.Parse("""{"toTransfer":true}""")).Should().BeTrue();
        JsonNode.DeepEquals(
                presets["notExported"],
                JsonNode.Parse("""{"status":"Approved","exported":false,"size":50}"""))
            .Should().BeTrue();
        JsonNode.DeepEquals(presets["everyone"], JsonNode.Parse("""{"scope":null}""")).Should().BeTrue();
    }

    [Fact]
    public void Presets_AlwaysCarryTheRequiredMembers() {
        var preset = Export()["methods"]!["invoices.byAccount"]!["presets"]!["main"];

        JsonNode.DeepEquals(preset, JsonNode.Parse("""{"account":"main","year":0}""")).Should().BeTrue();
    }

    [Fact]
    public void SortKeys_AreTheKeysOfTheHandlersOneSortMap() {
        var methods = Export()["methods"]!;

        JsonNode.DeepEquals(
                methods["invoices.list"]!["sortKeys"],
                JsonNode.Parse("""
                               [{"key":"createdAt","direction":"descending","default":true},
                                {"key":"number","direction":"ascending"}]
                               """))
            .Should().BeTrue();
        // Two sort maps: the export cannot tell which one the sort parameter uses (the generator warns, ELRPC012).
        methods["invoices.byAccount"]!.AsObject().ContainsKey("sortKeys").Should().BeFalse();
    }

    [Fact]
    public void Constants_AreExportedPerCamelCasedModule_WithCanonicalValues() {
        var constants = Export()["constants"]!.AsObject();

        constants.Select(static m => m.Key).Should().Equal("archive", "billing");
        JsonNode.DeepEquals(
                constants["billing"],
                JsonNode.Parse("""
                               {"currencies":["EUR","CHF"],"defaultStatus":"Approved","maxAmount":999999.99,"maxReceipts":10}
                               """))
            .Should().BeTrue();
        constants["billing"]!.AsObject().Select(static c => c.Key)
            .Should().Equal("currencies", "defaultStatus", "maxAmount", "maxReceipts");
    }

    [Fact]
    public void UploadLimits_TravelNextToTheUploadMarker() {
        var properties = Export()["methods"]!["statements.import"]!["params"]!["properties"]!;

        var statement = properties["statement"]!;
        statement["x-elarion-upload"]!.GetValue<bool>().Should().BeTrue();
        statement["x-elarion-max-bytes"]!.GetValue<long>().Should().Be(1024);
        statement["x-elarion-content-types"]!.AsArray().Select(static t => t!.GetValue<string>())
            .Should().Equal("text/csv", "application/xml");
        // A parameter target is read as well; a limit not declared is not written.
        properties["signature"]!["x-elarion-content-types"]!.AsArray().Select(static t => t!.GetValue<string>())
            .Should().Equal("application/pdf");
        properties["signature"]!.AsObject().ContainsKey("x-elarion-max-bytes").Should().BeFalse();
        // On a collection the limits apply to every file; the collection keeps its own maxItems.
        properties["receipts"]!["maxItems"]!.GetValue<int>().Should().Be(10);
        properties["receipts"]!["items"]!["x-elarion-max-bytes"]!.GetValue<long>().Should().Be(2048);
        properties["plain"]!.AsObject().ContainsKey("x-elarion-max-bytes").Should().BeFalse();
        // Accepted extensions travel the same way, alone when nothing else is declared.
        var workbook = properties["workbooks"]!["items"]!.AsObject();
        workbook["x-elarion-file-extensions"]!.AsArray().Select(static t => t!.GetValue<string>())
            .Should().Equal(".xlsx", ".xls");
        workbook.ContainsKey("x-elarion-max-bytes").Should().BeFalse();
        workbook.ContainsKey("x-elarion-content-types").Should().BeFalse();
        properties["statement"]!.AsObject().ContainsKey("x-elarion-file-extensions").Should().BeFalse();
    }

    [Fact]
    public void NoDeclarations_LeaveTheSchemaByteIdentical() {
        var without = JsonRpcSchemaExporter.Generate(Dispatcher(), Options);
        var empty = JsonRpcSchemaExporter.Generate(Dispatcher(), Options, new JsonRpcSchemaExportOptions {
            HandlerTypes = new Dictionary<Type, Type>(),
            ClientConstants = []
        });

        empty.Should().Be(without);
        without.Should().NotContain("\"constants\"").And.NotContain("\"presets\"").And.NotContain("\"sortKeys\"");
    }

    public sealed class BrokenPreset {
        [RequestPreset("broken")]
        public static readonly AccountQuery? Broken = null;
    }

    [Fact]
    public void APresetThatHoldsNoRequest_FailsTheExport() {
        var exportOptions = new JsonRpcSchemaExportOptions {
            HandlerTypes = new Dictionary<Type, Type> { [typeof(AccountQuery)] = typeof(BrokenPreset) }
        };

        var export = () => JsonRpcSchemaExporter.Generate(Dispatcher(), Options, exportOptions);

        export.Should().Throw<InvalidOperationException>().WithMessage("*'broken'*invoices.byAccount*");
    }
}
