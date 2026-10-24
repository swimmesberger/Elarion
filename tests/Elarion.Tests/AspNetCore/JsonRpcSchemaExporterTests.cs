using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Modules;
using Elarion.JsonRpc;
using Elarion.Tests.Features;
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
            "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "enum"
        ];
        foreach (var keyword in constraintKeywords) untouched.TryGetProperty(keyword, out _).Should().BeFalse();
    }

    [Fact]
    public void Generate_CarriesExplicitErrorMessages_KeyedByConstraintKeyword() {
        var fields = ExportParamsFields<MessagedRequest>("sample.messaged");

        // The explicit message rides next to the keyword it explains; the keyword itself is unchanged.
        var code = fields.GetProperty("code");
        code.GetProperty("pattern").GetString().Should().Be("^[A-Z]{3}$");
        var codeMessages = code.GetProperty("x-elarion-error-messages");
        codeMessages.GetProperty("pattern").GetString().Should().Be("Code must be three capital letters.");
        // Placeholders are formatted exactly as the server's validator formats them: {0} display name, {1} bound.
        codeMessages.GetProperty("maxLength").GetString().Should().Be("Code must be at most 20 characters.");

        // One attribute producing two keywords carries its message on both; [Display(Name)] is the display name.
        var labelMessages = fields.GetProperty("label").GetProperty("x-elarion-error-messages");
        labelMessages.GetProperty("minLength").GetString().Should().Be("Shown label needs 2 to 10 characters.");
        labelMessages.GetProperty("maxLength").GetString().Should().Be("Shown label needs 2 to 10 characters.");

        fields.GetProperty("quantity").GetProperty("x-elarion-error-messages").GetProperty("minimum")
            .GetString().Should().Be("Quantity must be between 1 and 9.");
        fields.GetProperty("email").GetProperty("x-elarion-error-messages").GetProperty("format")
            .GetString().Should().Be("Enter a valid address.");
        fields.GetProperty("mode").GetProperty("x-elarion-error-messages").GetProperty("enum")
            .GetString().Should().Be("Pick fast or slow.");

        // A resource-based message is resolved at export time.
        fields.GetProperty("reference").GetProperty("x-elarion-error-messages").GetProperty("pattern")
            .GetString().Should().Be("Reference must start with R.");
    }

    [Fact]
    public void Generate_ExportedErrorMessages_MatchTheServerValidatorsMessages() {
        var fields = ExportParamsFields<MessagedRequest>("sample.messaged");
        var invalid = new MessagedRequest {
            Code = "abc", Label = "x", Quantity = 0, Email = "nope", Mode = "medium", Reference = "x",
            Note = "ok", Contact = "a@b.c", Overridden = "ok"
        };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(invalid, new ValidationContext(invalid), results, validateAllProperties: true);

        // The client must show the text the server would send for the same violation.
        string ExportedMessage(string field, string keyword) =>
            fields.GetProperty(field).GetProperty("x-elarion-error-messages").GetProperty(keyword).GetString()!;

        results.Select(static result => result.ErrorMessage).Should().BeEquivalentTo(
            ExportedMessage("code", "pattern"),
            ExportedMessage("label", "minLength"),
            ExportedMessage("quantity", "minimum"),
            ExportedMessage("email", "format"),
            ExportedMessage("mode", "enum"),
            ExportedMessage("reference", "pattern"));
    }

    [Fact]
    public void Generate_AnchorsPatternsAndExportsRequiredStringsAsNotBlank() {
        var fields = ExportParamsFields<PatternRequest>("sample.patterns");
        string Pattern(string field) => fields.GetProperty(field).GetProperty("pattern").GetString()!;

        // The server matches the whole value; JSON Schema searches, so an unanchored pattern is anchored on export.
        Pattern("letters").Should().Be("^(?:[a-z]+)$");
        // An already-anchored pattern stays byte-identical; a top-level alternation is not anchored by its ^/$.
        Pattern("anchored").Should().Be("^[a-z]+$");
        Pattern("alternation").Should().Be("^(?:^a|b$)$");
        // [Required] on a string is the BCL's not-blank constraint; with a pattern, both apply to one keyword.
        Pattern("title").Should().Be(@"^(?=[\s\S]*\S)(?:[\s\S]*)$");
        Pattern("code").Should().Be(@"^(?=[\s\S]*\S)(?:^[A-Z]+$)$");
        fields.GetProperty("code").GetProperty("x-elarion-error-messages").GetProperty("pattern").GetString()
            .Should().Be("Enter a code.");
        // Empty strings allowed, or a non-string member: [Required] adds nothing (requiredness is nullability).
        fields.GetProperty("free").TryGetProperty("pattern", out _).Should().BeFalse();
        fields.GetProperty("count").TryGetProperty("pattern", out _).Should().BeFalse();
    }

    [Fact]
    public void Generate_ResultSchemas_KeepPatternsAsDeclared_AndAddNoNotBlank() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, PatternRequest>("sample.read", (_, _, _) => throw new NotSupportedException())
            .Freeze();
        using var doc = JsonDocument.Parse(JsonRpcSchemaExporter.Generate(dispatcher, options));
        var fields = doc.RootElement.GetProperty("methods").GetProperty("sample.read").GetProperty("result")
            .GetProperty("properties");

        // The server never validates results, but the client parses them: a stored value from before a constraint must
        // not fail a read, so the request-side tightening stays off.
        fields.GetProperty("letters").GetProperty("pattern").GetString().Should().Be("[a-z]+");
        fields.GetProperty("code").GetProperty("pattern").GetString().Should().Be("^[A-Z]+$");
        fields.GetProperty("title").TryGetProperty("pattern", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("letters", "abc")]
    [InlineData("letters", "abc1")]
    [InlineData("letters", "1abc")]
    [InlineData("alternation", "a")]
    [InlineData("alternation", "ab")]
    [InlineData("alternation", "b")]
    [InlineData("title", "")]
    [InlineData("title", " \t ")]
    [InlineData("title", " x ")]
    [InlineData("code", "   ")]
    [InlineData("code", "ABC")]
    [InlineData("code", "AB C")]
    public void Generate_ExportedPatterns_AcceptExactlyWhatTheServerAccepts(string field, string value) {
        var fields = ExportParamsFields<PatternRequest>("sample.patterns");
        var pattern = fields.GetProperty(field).GetProperty("pattern").GetString()!;
        var member = typeof(PatternRequest).GetProperty(char.ToUpperInvariant(field[0]) + field[1..])!;
        var server = member.GetCustomAttributes(true).OfType<ValidationAttribute>().All(a => a.IsValid(value));

        // Search semantics, as a JSON Schema validator (and the generated Zod regex) applies the pattern.
        System.Text.RegularExpressions.Regex.IsMatch(value, pattern).Should().Be(server);
    }

    [Fact]
    public void Generate_ExportsClientRequirementsReadOnlyAndTagsPerMethod() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        static ValueTask<Result<PingResponse>> Ok<TRequest>(TRequest _, IServiceProvider __, CancellationToken ___) =>
            ValueTask.FromResult<Result<PingResponse>>(new PingResponse("ok"));
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<ImportCommand, PingResponse>("ledger.import", Ok, client: new HandlerClientMetadata {
                RequiresAuthentication = true,
                Permissions = ["ledger.write", "ledger.import"],
                Roles = ["Clerk"],
                HasServerChecks = true,
                Tags = new Dictionary<string, string> { ["viewAs"] = "refused", ["timeout"] = "180s" }
            })
            .MapDelegate<StatusQuery, PingResponse>("ledger.status", Ok,
                client: new HandlerClientMetadata { AllowAnonymous = true })
            .MapDelegate<PingRequest, PingResponse>("ledger.ping", Ok)
            .Freeze();

        using var doc = JsonDocument.Parse(JsonRpcSchemaExporter.Generate(dispatcher, options));
        var methods = doc.RootElement.GetProperty("methods");

        var import = methods.GetProperty("ledger.import");
        var requires = import.GetProperty("requires");
        requires.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        requires.GetProperty("permissions").EnumerateArray().Select(static p => p.GetString())
            .Should().Equal("ledger.import", "ledger.write");
        requires.GetProperty("roles").EnumerateArray().Select(static r => r.GetString()).Should().Equal("Clerk");
        requires.GetProperty("serverChecks").GetBoolean().Should().BeTrue();
        requires.TryGetProperty("anonymous", out _).Should().BeFalse();
        import.GetProperty("tags").EnumerateObject().Select(static t => $"{t.Name}={t.Value.GetString()}")
            .Should().Equal("timeout=180s", "viewAs=refused");
        import.TryGetProperty("readOnly", out _).Should().BeFalse();

        // An IQuery request is read-only; an anonymous operation lists no other requirement.
        var status = methods.GetProperty("ledger.status");
        status.GetProperty("readOnly").GetBoolean().Should().BeTrue();
        status.GetProperty("requires").EnumerateObject().Select(static p => p.Name).Should().Equal("anonymous");

        // A route without client metadata (and a non-query request) gains no member.
        methods.GetProperty("ledger.ping").EnumerateObject().Select(static p => p.Name).Should().Equal("params", "result");
    }

    [Fact]
    public void Generate_OmitsDefaultErrorMessages() {
        var fields = ExportParamsFields<MessagedRequest>("sample.messaged");

        // The framework's default messages stay out of the schema: only an explicit message is contract.
        fields.GetProperty("note").GetProperty("maxLength").GetInt32().Should().Be(5);
        fields.GetProperty("note").TryGetProperty("x-elarion-error-messages", out _).Should().BeFalse();
        fields.GetProperty("contact").GetProperty("format").GetString().Should().Be("email");
        fields.GetProperty("contact").TryGetProperty("x-elarion-error-messages", out _).Should().BeFalse();
    }

    [Fact]
    public void Generate_LaterAttributeWithoutMessage_DropsEarlierMessageForTheSameKeyword() {
        var fields = ExportParamsFields<MessagedRequest>("sample.messaged");

        // Keywords are last-wins, so a message must not outlive the bound it described.
        var overridden = fields.GetProperty("overridden");
        overridden.GetProperty("maxLength").GetInt32().Should().Be(10);
        overridden.TryGetProperty("x-elarion-error-messages", out _).Should().BeFalse();
    }

    private static JsonElement ExportParamsFields<TRequest>(string method) where TRequest : class {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<TRequest, PingResponse>(
                method,
                (_, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse("ok")))
            .Freeze();

        using var doc = JsonDocument.Parse(JsonRpcSchemaExporter.Generate(dispatcher, options));
        return doc.RootElement.GetProperty("methods").GetProperty(method).GetProperty("params")
            .GetProperty("properties").Clone();
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
                    new ClientModuleManifest { Name = "Invoicing", Enabled = true },
                    new ClientModuleManifest { Name = "Clients", Enabled = true },
                    new ClientModuleManifest { Name = "Experiments", Enabled = false }
                ]
            },
            FeatureFlags = new StubFeatureFlagCatalog(
                StubFeatureFlagCatalog.Flag("late-fees", "Invoicing"),
                StubFeatureFlagCatalog.Flag("client-portal-v2", "Clients"),
                StubFeatureFlagCatalog.Flag("bulk-import", "Clients"),
                StubFeatureFlagCatalog.Flag("internal-only", "Clients", expose: false),
                StubFeatureFlagCatalog.Flag("beta-x", "Experiments")),
            PermissionCatalog = new FakePermissionCatalog(
                [
                    new PermissionCatalogEntry { Permission = "invoices.read", Resource = "invoices", Verb = "read" },
                    new PermissionCatalogEntry { Permission = "clients.read", Resource = "clients", Verb = "read" },
                    // Duplicate across modules — must be deduplicated in the export.
                    new PermissionCatalogEntry { Permission = "clients.read", Resource = "clients", Verb = "read" }
                ],
                ["billing-admin", "auditor"])
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
        plain.Should().NotContain("\"events\"");
        withEmptyOptions.Should().Be(plain);
    }

    private sealed record InvoiceChangedEvent {
        public required Guid InvoiceId { get; init; }
    }

    private sealed record ImportProgressEvent {
        public required int Processed { get; init; }
        public required int Total { get; init; }
    }

    [Fact]
    public void Generate_EmitsEventsBlock_TopicsSortedWithPayloadSchemas() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)))
            .Freeze();

        var exportOptions = new JsonRpcSchemaExportOptions {
            ClientEventTopics = new ClientEventTopicManifest {
                Topics = [
                    new ClientEventTopicManifestEntry {
                        Name = "invoicing.importProgress", EventType = typeof(ImportProgressEvent)
                    },
                    new ClientEventTopicManifestEntry {
                        Name = "invoicing.invoiceChanged", EventType = typeof(InvoiceChangedEvent)
                    }
                ]
            }
        };

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options, exportOptions);

        using var doc = JsonDocument.Parse(schema);
        var events = doc.RootElement.GetProperty("events");

        // Topics in ordinal order, each carrying its payload schema with wire-named properties.
        events.EnumerateObject().Select(static p => p.Name)
            .Should().Equal("invoicing.importProgress", "invoicing.invoiceChanged");
        var payload = events.GetProperty("invoicing.invoiceChanged").GetProperty("payload");
        payload.GetProperty("properties").TryGetProperty("invoiceId", out _).Should().BeTrue();
        events.GetProperty("invoicing.importProgress").GetProperty("payload")
            .GetProperty("properties").TryGetProperty("processed", out _).Should().BeTrue();
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
            new() { Module = "Test", Permissions = permissions, Roles = roles }
        ];
    }

    private sealed record PingRequest(string Message);

    private sealed record PingResponse(string Message);

    private sealed record ConstrainedRequest {
        [StringLength(100, MinimumLength = 3)] public required string Name { get; init; }

        [RegularExpression("^[a-z0-9-]+$")] public required string Slug { get; init; }

        [Range(1, 500)] public required int Quantity { get; init; }

        [Range(0d, 1d, MinimumIsExclusive = true, MaximumIsExclusive = true)]
        public required double Ratio { get; init; }

        [Range(typeof(decimal), "0.5", "99.9")]
        public required decimal Price { get; init; }

        [EmailAddress] public required string Email { get; init; }

        [AllowedValues("smtp", "office365")] public required string Backend { get; init; }

        [MaxLength(10)] public required IReadOnlyList<string> Tags { get; init; }

        public required string Untouched { get; init; }
    }

    private sealed record MessagedRequest {
        [RegularExpression("^[A-Z]{3}$", ErrorMessage = "Code must be three capital letters.")]
        [StringLength(20, ErrorMessage = "{0} must be at most {1} characters.")]
        public required string Code { get; init; }

        [Display(Name = "Shown label")]
        [StringLength(10, MinimumLength = 2, ErrorMessage = "{0} needs {2} to {1} characters.")]
        public required string Label { get; init; }

        [Range(1, 9, ErrorMessage = "{0} must be between {1} and {2}.")]
        public required int Quantity { get; init; }

        [EmailAddress(ErrorMessage = "Enter a valid address.")]
        public required string Email { get; init; }

        [AllowedValues("fast", "slow", ErrorMessage = "Pick fast or slow.")]
        public required string Mode { get; init; }

        [RegularExpression("^R", ErrorMessageResourceType = typeof(MessageResources),
            ErrorMessageResourceName = nameof(MessageResources.ReferencePattern))]
        public required string Reference { get; init; }

        [StringLength(5)] public required string Note { get; init; }

        [EmailAddress] public required string Contact { get; init; }

        [MaxLength(5, ErrorMessage = "Too long.")]
        [StringLength(10)]
        public required string Overridden { get; init; }
    }

    private sealed record ImportCommand : ICommand;

    private sealed record StatusQuery : IQuery;

    private sealed record PatternRequest {
        [RegularExpression("[a-z]+")] public required string Letters { get; init; }

        [RegularExpression("^[a-z]+$")] public required string Anchored { get; init; }

        [RegularExpression("^a|b$")] public required string Alternation { get; init; }

        [Required] public required string Title { get; init; }

        [RegularExpression("^[A-Z]+$")]
        [Required(ErrorMessage = "Enter a code.")]
        public required string Code { get; init; }

        [Required(AllowEmptyStrings = true)] public required string Free { get; init; }

        [Required] public required int Count { get; init; }
    }

    // Stands in for a generated .resx designer class: a public static string getter per message.
    private static class MessageResources {
        public static string ReferencePattern => "{0} must start with R.";
    }

    private sealed record DescribedRequest {
        [System.ComponentModel.Description("Human-readable message.")]
        public required string Message { get; init; }
    }
}
