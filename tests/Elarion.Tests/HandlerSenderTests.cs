using AwesomeAssertions;
using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests;

/// <summary>
/// The typed in-process mediator send: <see cref="IHandlerSender"/> routes a request to its handler <b>by type</b>,
/// resolving the decorated <see cref="IHandler{TRequest,TResponse}"/> from the ambient scope (the caller's
/// transaction). It is the typed replacement for the removed <c>IDomainEventBus.RequestAsync</c> (ADR-0010).
/// </summary>
public sealed class HandlerSenderTests {
    private sealed record Double(int Value);

    private sealed class DoublingHandler : IHandler<Double, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(Double request, CancellationToken ct) =>
            request.Value < 0
                ? ValueTask.FromResult<Result<int>>(AppError.Validation("negative"))
                : ValueTask.FromResult<Result<int>>(request.Value * 2);
    }

    private static ServiceProvider Build() =>
        new ServiceCollection()
            .AddElarionHandlerSender()
            .AddScoped<IHandler<Double, Result<int>>, DoublingHandler>()
            .BuildServiceProvider();

    [Fact]
    public async Task SendAsync_RoutesToHandlerByType_AndReturnsResult() {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IHandlerSender>();

        var result = await sender.SendAsync<Double, int>(new Double(21), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public async Task SendAsync_PropagatesHandlerFailure() {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IHandlerSender>();

        var result = await sender.SendAsync<Double, int>(new Double(-1), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }
}
