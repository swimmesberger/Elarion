using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Modules;
using Elarion.JsonRpc;
using Xunit;

namespace Elarion.Tests.AspNetCore;

public sealed class JsonRpcSchemaExporterTests {
    [Fact]
    public void Generate_FrozenDispatcher_ExportsRegisteredMethods() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)))
            .Freeze();

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options);

        schema.Should().Contain("\"sample.ping\"");
        schema.Should().Contain("\"params\"");
        schema.Should().Contain("\"result\"");
        schema.Should().Contain("\"message\"");
    }

    [Fact]
    public void Generate_UnfrozenDispatcher_ThrowsActionableError() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)));

        var act = () => JsonRpcSchemaExporter.Generate(dispatcher, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dispatcher is not frozen*");
    }

    [Fact]
    public void Generate_EmptyDispatcher_ThrowsActionableError() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options).Freeze();

        var act = () => JsonRpcSchemaExporter.Generate(dispatcher, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no registered methods*");
    }

    private sealed record UploadCommand {
        public required string Container { get; init; }
        public required ElarionFile File { get; init; }
    }

    [Fact]
    public void Generate_FileResponse_EmitsBase64Envelope() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, ElarionFile>(
                "files.download",
                (_, _, _) => ValueTask.FromResult<Result<ElarionFile>>(
                    new ElarionFile(new byte[] { 1 }, "text/csv")))
            .Freeze();

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options);

        using var doc = JsonDocument.Parse(schema);
        var result = doc.RootElement.GetProperty("methods").GetProperty("files.download").GetProperty("result");

        // The converter-backed type would export as an opaque schema; the exporter substitutes the fixed
        // envelope, so generated clients get a real contract (data rides format: byte like [Base64String]),
        // marked x-elarion-file so the TypeScript generator maps it to a native File.
        result.GetProperty("x-elarion-file").GetBoolean().Should().BeTrue();
        var properties = result.GetProperty("properties");
        properties.GetProperty("contentType").GetProperty("type").GetString().Should().Be("string");
        properties.GetProperty("data").GetProperty("format").GetString().Should().Be("byte");
        result.GetProperty("required").EnumerateArray().Select(static e => e.GetString())
            .Should().BeEquivalentTo("contentType", "data");
    }

    [Fact]
    public void Generate_FilePropertyInParams_EmitsBase64Envelope() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<UploadCommand, PingResponse>(
                "files.upload",
                (_, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse("ok")))
            .Freeze();

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options);

        using var doc = JsonDocument.Parse(schema);
        var file = doc.RootElement.GetProperty("methods").GetProperty("files.upload")
            .GetProperty("params").GetProperty("properties").GetProperty("file");

        file.GetProperty("properties").GetProperty("data").GetProperty("format").GetString().Should().Be("byte");
        file.GetProperty("required").EnumerateArray().Select(static e => e.GetString())
            .Should().BeEquivalentTo("contentType", "data");
    }

    [Fact]
    public void Generate_InjectsPropertyDescriptions_FromDescriptionAttribute() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<DescribedRequest, PingResponse>(
                "sample.described",
                (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)))
            .Freeze();

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options);

        // [Description] becomes a schema "description" (JSDoc source for the generated TypeScript client).
        schema.Should().Contain("Human-readable message.");
    }

    [Fact]
    public void Generate_InjectsConstraintKeywords_FromDataAnnotations() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<ConstrainedRequest, PingResponse>(
                "sample.constrained",
                (_, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse("ok")))
            .Freeze();

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options);

        using var doc = JsonDocument.Parse(schema);
        var properties = doc.RootElement.GetProperty("methods").GetProperty("sample.constrained")
            .GetProperty("params").GetProperty("properties");

        // [StringLength(100, MinimumLength = 3)] → minLength/maxLength.
        var name = properties.GetProperty("name");
        name.GetProperty("minLength").GetInt32().Should().Be(3);
        name.GetProperty("maxLength").GetInt32().Should().Be(100);

        // [RegularExpression] → pattern.
        properties.GetProperty("slug").GetProperty("pattern").GetString().Should().Be("^[a-z0-9-]+$");

        // [Range(1, 500)] → inclusive minimum/maximum.
        var quantity = properties.GetProperty("quantity");
        quantity.GetProperty("minimum").GetDecimal().Should().Be(1);
        quantity.GetProperty("maximum").GetDecimal().Should().Be(500);

        // Exclusive [Range] bounds land in exclusiveMinimum/exclusiveMaximum, not the inclusive keywords.
        var ratio = properties.GetProperty("ratio");
        ratio.GetProperty("exclusiveMinimum").GetDecimal().Should().Be(0);
        ratio.GetProperty("exclusiveMaximum").GetDecimal().Should().Be(1);
        ratio.TryGetProperty("minimum", out _).Should().BeFalse();
        ratio.TryGetProperty("maximum", out _).Should().BeFalse();

        // The Range(typeof(decimal), "…", "…") string form is parsed invariantly and emitted as JSON numbers.
        var price = properties.GetProperty("price");
        price.GetProperty("minimum").ValueKind.Should().Be(JsonValueKind.Number);
        price.GetProperty("minimum").GetDecimal().Should().Be(0.5m);
        price.GetProperty("maximum").GetDecimal().Should().Be(99.9m);

        // [EmailAddress] → format: "email".
        properties.GetProperty("email").GetProperty("format").GetString().Should().Be("email");

        // [AllowedValues] → enum, in declaration order (a configuration-variant vocabulary, backend names, …).
        var backend = properties.GetProperty("backend");
        backend.GetProperty("enum").EnumerateArray().Select(static value => value.GetString())
            .Should().Equal("smtp", "office365");

        // [MaxLength] on an array schema becomes maxItems, never maxLength.
        var tags = properties.GetProperty("tags");
        tags.GetProperty("maxItems").GetInt32().Should().Be(10);
        tags.TryGetProperty("maxLength", out _).Should().BeFalse();

        // An unannotated property carries no constraint keywords.
        var untouched = properties.GetProperty("untouched");
        string[] constraintKeywords = [
            "minLength", "maxLength", "minItems", "maxItems", "pattern", "format",
            "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "enum",
        ];
        foreach (var keyword in constraintKeywords) {
            untouched.TryGetProperty(keyword, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void Generate_EmitsCapabilitiesVocabulary_EnabledModulesSortedAndStructuredPermissions() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)))
            .Freeze();

        var exportOptions = new JsonRpcSchemaExportOptions {
            ClientCapabilities = new ClientCapabilityManifest {
                Modules = [
                    new ClientModuleManifest { Name = "Invoicing", Enabled = true, Features = ["late-fees"] },
                    new ClientModuleManifest { Name = "Clients", Enabled = true, Features = ["client-portal-v2", "bulk-import"] },
                    new ClientModuleManifest { Name = "Experiments", Enabled = false, Features = ["beta-x"] },
                ],
            },
            PermissionCatalog = new FakePermissionCatalog(
                [
                    new PermissionCatalogEntry { Permission = "invoices.read", Resource = "invoices", Verb = "read" },
                    new PermissionCatalogEntry { Permission = "clients.read", Resource = "clients", Verb = "read" },
                    // Duplicate across modules — must be deduplicated in the export.
                    new PermissionCatalogEntry { Permission = "clients.read", Resource = "clients", Verb = "read" },
                ],
                ["billing-admin", "auditor"]),
        };

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options, exportOptions);

        using var doc = JsonDocument.Parse(schema);
        var capabilities = doc.RootElement.GetProperty("capabilities");

        // Enabled modules only, ordinally sorted; each carries its sorted feature names.
        var modules = capabilities.GetProperty("modules");
        modules.EnumerateObject().Select(static p => p.Name).Should().Equal("Clients", "Invoicing");
        modules.GetProperty("Clients").GetProperty("features").EnumerateArray()
            .Select(static f => f.GetString()).Should().Equal("bulk-import", "client-portal-v2");
        modules.TryGetProperty("Experiments", out _).Should().BeFalse();

        // Structured, deduplicated, sorted permission entries; sorted roles.
        var permissions = capabilities.GetProperty("permissions").EnumerateArray().ToArray();
        permissions.Select(static p => p.GetProperty("permission").GetString())
            .Should().Equal("clients.read", "invoices.read");
        permissions[0].GetProperty("resource").GetString().Should().Be("clients");
        permissions[0].GetProperty("verb").GetString().Should().Be("read");
        capabilities.GetProperty("roles").EnumerateArray().Select(static r => r.GetString())
            .Should().Equal("auditor", "billing-admin");
    }

    [Fact]
    public void Generate_OmitsCapabilitiesBlock_WhenNoVocabularySupplied() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)))
            .Freeze();

        // No options, and options with nothing in them, must both stay byte-identical to the plain export.
        var plain = JsonRpcSchemaExporter.Generate(dispatcher, options);
        var withEmptyOptions = JsonRpcSchemaExporter.Generate(dispatcher, options, new JsonRpcSchemaExportOptions());

        plain.Should().NotContain("\"capabilities\"");
        withEmptyOptions.Should().Be(plain);
    }

    private sealed class FakePermissionCatalog(
        IReadOnlyList<PermissionCatalogEntry> permissions,
        IReadOnlyList<string> roles) : IPermissionCatalog {
        public IReadOnlyList<string> Permissions { get; } =
            [.. permissions.Select(static e => e.Permission).Distinct().Order()];

        public IReadOnlyList<string> Roles { get; } = [.. roles.Order()];

        public IReadOnlyDictionary<string, IReadOnlyList<string>> ByResource { get; } =
            new Dictionary<string, IReadOnlyList<string>>();

        public IReadOnlyDictionary<string, IReadOnlyList<string>> ByVerb { get; } =
            new Dictionary<string, IReadOnlyList<string>>();

        public IReadOnlyList<PermissionCatalogModule> Modules { get; } = [
            new PermissionCatalogModule { Module = "Test", Permissions = permissions, Roles = roles },
        ];
    }

    private sealed record PingRequest(string Message);

    private sealed record PingResponse(string Message);

    private sealed record ConstrainedRequest {
        [StringLength(100, MinimumLength = 3)]
        public required string Name { get; init; }

        [RegularExpression("^[a-z0-9-]+$")]
        public required string Slug { get; init; }

        [Range(1, 500)]
        public required int Quantity { get; init; }

        [Range(0d, 1d, MinimumIsExclusive = true, MaximumIsExclusive = true)]
        public required double Ratio { get; init; }

        [Range(typeof(decimal), "0.5", "99.9")]
        public required decimal Price { get; init; }

        [EmailAddress]
        public required string Email { get; init; }

        [AllowedValues("smtp", "office365")]
        public required string Backend { get; init; }

        [MaxLength(10)]
        public required IReadOnlyList<string> Tags { get; init; }

        public required string Untouched { get; init; }
    }

    private sealed record DescribedRequest {
        [System.ComponentModel.Description("Human-readable message.")]
        public required string Message { get; init; }
    }
}
