using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Identity;
using Elarion.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests;

/// <summary>
/// The typed-direct transport entry point: it creates a seeded scope, resolves the decorated handler, invokes
/// it, and disposes the scope — so a non-RPC transport (gRPC, console) needs no hand-rolled scope plumbing and
/// still gets <c>ICurrentUser</c> from the captured principal.
/// </summary>
public sealed class HandlerInvokerTests {
    private sealed record WhoAmIQuery;

    private sealed record WhoAmIResponse(string UserId, bool Authenticated);

    private sealed class WhoAmIHandler(ICurrentUser user) : IHandler<WhoAmIQuery, Result<WhoAmIResponse>> {
        public ValueTask<Result<WhoAmIResponse>> HandleAsync(WhoAmIQuery request, CancellationToken ct) =>
            // UserId has no value for an anonymous principal (it throws), so only read it when authenticated.
            ValueTask.FromResult<Result<WhoAmIResponse>>(
                new WhoAmIResponse(user.IsAuthenticated ? user.UserId : "", user.IsAuthenticated));
    }

    [Fact]
    public async Task InvokeAsync_SeedsCurrentUserFromContext_AndReturnsResult() {
        using var provider = new ServiceCollection()
            .AddElarionClaimsCurrentUser()
            .AddScoped<IHandler<WhoAmIQuery, Result<WhoAmIResponse>>, WhoAmIHandler>()
            .BuildServiceProvider();
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-9")], authenticationType: "test")));

        var result = await HandlerInvoker.InvokeAsync<WhoAmIQuery, WhoAmIResponse>(
            provider, new WhoAmIQuery(), context, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be("user-9");
        result.Value.Authenticated.Should().BeTrue();
    }

    private sealed record InferredWhoAmIQuery : IQuery<InferredWhoAmIQuery, WhoAmIResponse>;

    private sealed class InferredWhoAmIHandler(ICurrentUser user) : IHandler<InferredWhoAmIQuery, Result<WhoAmIResponse>> {
        public ValueTask<Result<WhoAmIResponse>> HandleAsync(InferredWhoAmIQuery request, CancellationToken ct) =>
            ValueTask.FromResult<Result<WhoAmIResponse>>(
                new WhoAmIResponse(user.IsAuthenticated ? user.UserId : "", user.IsAuthenticated));
    }

    [Fact]
    public async Task InvokeAsync_SelfTypedMarkerInfersBothTypeArguments() {
        using var provider = new ServiceCollection()
            .AddElarionClaimsCurrentUser()
            .AddScoped<IHandler<InferredWhoAmIQuery, Result<WhoAmIResponse>>, InferredWhoAmIHandler>()
            .BuildServiceProvider();

        Result<WhoAmIResponse> result = await HandlerInvoker.InvokeAsync(
            provider, new InferredWhoAmIQuery(), context: null, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Authenticated.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_NoContext_CurrentUserIsAnonymous() {
        using var provider = new ServiceCollection()
            .AddElarionClaimsCurrentUser()
            .AddScoped<IHandler<WhoAmIQuery, Result<WhoAmIResponse>>, WhoAmIHandler>()
            .BuildServiceProvider();

        var result = await HandlerInvoker.InvokeAsync<WhoAmIQuery, WhoAmIResponse>(
            provider, new WhoAmIQuery(), context: null, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Authenticated.Should().BeFalse();
    }
}
