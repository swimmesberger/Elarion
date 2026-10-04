using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Elarion.Grpc;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Abstractions;

public sealed class AppErrorCodeTests {
    public sealed record SeatTaken(int Seat);

    [Theory]
    [InlineData(ErrorKind.Validation, "validation")]
    [InlineData(ErrorKind.NotFound, "not_found")]
    [InlineData(ErrorKind.Conflict, "conflict")]
    [InlineData(ErrorKind.Forbidden, "forbidden")]
    [InlineData(ErrorKind.Unauthorized, "unauthorized")]
    [InlineData(ErrorKind.BusinessRule, "business_rule")]
    [InlineData(ErrorKind.Internal, "internal")]
    public void Error_WithoutSpecificCode_CarriesTheKindDefaultCode(ErrorKind kind, string code) {
        AppError.Create(kind, "x").Code.Should().Be(code);
        ErrorCodes.ForKind(kind).Should().Be(code);
    }

    [Fact]
    public void EveryErrorKind_HasADefaultCode() {
        foreach (var kind in Enum.GetValues<ErrorKind>())
            ErrorCodes.IsValid(ErrorCodes.ForKind(kind)).Should().BeTrue();
    }

    [Fact]
    public void SpecificCodeAndData_AreCarriedByEveryFactory() {
        var data = new SeatTaken(4);

        AppError.Conflict("taken", "seat.taken", data).Should().Match<AppError>(e =>
            e.Kind == ErrorKind.Conflict && e.Code == "seat.taken" && ReferenceEquals(e.Data, data));
        AppError.NotFound("n", "client.missing").Code.Should().Be("client.missing");
        AppError.Forbidden("f", "plan.limit").Code.Should().Be("plan.limit");
        AppError.Unauthorized("u", "token.expired").Code.Should().Be("token.expired");
        AppError.BusinessRule("b", "invoice.closed").Code.Should().Be("invoice.closed");
        AppError.Internal("i", "upstream.down").Code.Should().Be("upstream.down");
        AppError.Validation("v", "token.malformed", data).Code.Should().Be("token.malformed");
        AppError.Validation("v", ["a"]).Code.Should().Be("validation");
        AppError.Validation("v", new Dictionary<string, string[]> { ["a"] = ["b"] }, "form.invalid").Code
            .Should().Be("form.invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("1abc")]
    [InlineData("a..b")]
    [InlineData("a.")]
    [InlineData(".a")]
    [InlineData("a-b")]
    [InlineData("a b")]
    public void InvalidCode_IsRejected(string code) {
        ErrorCodes.IsValid(code).Should().BeFalse();
        var act = () => AppError.NotFound("x", code);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a")]
    [InlineData("token.malformed")]
    [InlineData("idempotency.key_required")]
    [InlineData("v2.thing_9")]
    public void ValidCode_IsAccepted(string code) {
        ErrorCodes.IsValid(code).Should().BeTrue();
    }

    [Fact]
    public void Equality_TreatsAnExplicitDefaultCodeAsTheDefault() {
        AppError.NotFound("x").Should().Be(AppError.NotFound("x", "not_found"));
        AppError.NotFound("x").Should().NotBe(AppError.NotFound("x", "client.missing"));
    }

    [Fact]
    public void JsonRpc_AlwaysCarriesCodeAndData() {
        var plain = AppErrorMapper.ToRpcError(AppError.NotFound("gone"));
        plain.Code.Should().Be(-32001);
        plain.Data.Code.Should().Be("not_found");
        plain.Data.Data.Should().BeNull();

        var data = new SeatTaken(3);
        var coded = AppErrorMapper.ToRpcError(AppError.Conflict("taken", "seat.taken", data));
        coded.Data.Code.Should().Be("seat.taken");
        coded.Data.Data.Should().BeSameAs(data);

        AppErrorMapper.ToRpcError(AppError.Validation("v")).Data.Code.Should().Be("validation");
    }

    [Fact]
    public void JsonRpc_ProtocolErrors_CarryProtocolCodes() {
        RpcError.ParseError().Data.Code.Should().Be("parse_error");
        RpcError.InvalidRequest().Data.Code.Should().Be("invalid_request");
        RpcError.MethodNotFound().Data.Code.Should().Be("method_not_found");
        RpcError.InvalidParams().Data.Code.Should().Be("invalid_params");
        RpcError.InternalError().Data.Code.Should().Be("internal");
        JsonRpcResponse.MethodNotFound("1").Error!.Data.Code.Should().Be("method_not_found");
    }

    [Fact]
    public void JsonRpc_WireShape_IsCodeAndOptionalData() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => {
            o.TypeInfoResolvers.Add(JsonRpcJsonContext.Default);
            o.EnableReflectionFallback = true;
        });
        var options = services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>().Options;

        var bare = JsonSerializer.Serialize(
            JsonRpcResponse.FromError("1", AppErrorMapper.ToRpcError(AppError.NotFound("gone"))), options);
        using var bareDoc = JsonDocument.Parse(bare);
        var bareData = bareDoc.RootElement.GetProperty("error").GetProperty("data");
        bareData.GetProperty("code").GetString().Should().Be("not_found");
        bareData.TryGetProperty("data", out _).Should().BeFalse();

        var withPayload = JsonSerializer.Serialize(
            JsonRpcResponse.FromError("1", AppErrorMapper.ToRpcError(AppError.Validation("v", ["bad"]))), options);
        using var payloadDoc = JsonDocument.Parse(withPayload);
        var payload = payloadDoc.RootElement.GetProperty("error").GetProperty("data");
        payload.GetProperty("code").GetString().Should().Be("validation");
        payload.GetProperty("data").GetProperty("errors")[0].GetString().Should().Be("bad");
        payloadDoc.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
    }

    [Fact]
    public void Http_ProblemExtensions_AlwaysCarryCode_AndDataForTypedPayloads() {
        var plain = HttpAppErrorMapper.Extensions(AppError.NotFound("gone"));
        plain["code"].Should().Be("not_found");
        plain.ContainsKey("data").Should().BeFalse();

        var data = new SeatTaken(1);
        var typed = HttpAppErrorMapper.Extensions(AppError.Conflict("taken", "seat.taken", data));
        typed["code"].Should().Be("seat.taken");
        typed["data"].Should().BeSameAs(data);

        // The validation payload is the standard `errors` map, not repeated as `data`.
        HttpAppErrorMapper.Extensions(AppError.Validation("v", ["a"])).ContainsKey("data").Should().BeFalse();
    }

    [Fact]
    public void Grpc_Trailers_AlwaysCarryKindAndCode() {
        var exception = GrpcAppErrorTranslator.Default.Translate(AppError.Conflict("taken", "seat.taken"));

        exception.Trailers.GetValue(GrpcAppErrorTranslator.ErrorKindTrailerKey).Should().Be("conflict");
        exception.Trailers.GetValue(GrpcAppErrorTranslator.ErrorCodeTrailerKey).Should().Be("seat.taken");

        var plain = GrpcAppErrorTranslator.Default.Translate(AppError.NotFound("gone"));
        plain.Trailers.GetValue(GrpcAppErrorTranslator.ErrorCodeTrailerKey).Should().Be("not_found");
    }

    [Fact]
    public void IdempotencyReplay_RoundTripsTheCodeAndPayload_ToTheSameWireError() {
        var original = AppError.Conflict("taken", "seat.taken", new ValidationErrorData { Errors = ["seat 4"] });
        var stored = new StoredResult { Ok = false, Error = original };

        var json = JsonSerializer.Serialize(stored, ElarionFrameworkJsonContext.Default.StoredResult);
        var replayed = JsonSerializer.Deserialize(json, ElarionFrameworkJsonContext.Default.StoredResult)!.Error!;

        replayed.Code.Should().Be("seat.taken");
        replayed.Kind.Should().Be(ErrorKind.Conflict);
        var originalWire = AppErrorMapper.ToRpcError(original);
        var replayedWire = AppErrorMapper.ToRpcError(replayed);
        replayedWire.Code.Should().Be(originalWire.Code);
        replayedWire.Data.Code.Should().Be(originalWire.Data.Code);
        replayedWire.Data.Data.Should().BeOfType<JsonElement>().Which.GetRawText().Should().Contain("seat 4");

        // An error stored before codes existed has no code member and replays with its kind's default code.
        var legacy = JsonSerializer.Deserialize(
            """{"ok":false,"error":{"kind":"NotFound","message":"gone"}}""",
            ElarionFrameworkJsonContext.Default.StoredResult)!.Error!;
        legacy.Code.Should().Be("not_found");
    }
}
