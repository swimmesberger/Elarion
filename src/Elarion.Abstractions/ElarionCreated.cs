namespace Elarion.Abstractions;

/// <summary>
/// A creation-response envelope: declaring <c>Result&lt;ElarionCreated&lt;T&gt;&gt;</c> as a handler's response
/// says "a success created a new resource" once, and the generated <c>[HttpEndpoint]</c> mapping serves it as
/// <c>201 Created</c> — with the optional <see cref="Location"/> header — instead of <c>200 OK</c>, while the
/// response body stays the inner <see cref="Value"/>. Failures are unaffected and keep the central
/// <c>AppError</c> → RFC 7807 translation.
/// </summary>
/// <remarks>
/// The envelope is peeled by the HTTP transport, so it never appears on the HTTP wire and the OpenAPI document
/// truthfully advertises <c>201</c> with the inner value's schema. On the name-routed JSON surfaces (a handler
/// that also carries <c>[Handler]</c>) there is no status code to express, so the envelope serializes as a
/// plain object carrying <see cref="Value"/> and <see cref="Location"/>; prefer a plain response type for
/// operations designed for those transports.
/// </remarks>
/// <example>
/// <code>
/// [HttpEndpoint("clients")]
/// public sealed class CreateClient : IHandler&lt;CreateClient.Command, Result&lt;ElarionCreated&lt;CreateClient.Response&gt;&gt;&gt; {
///     public sealed record Command : ICommand { public required string Name { get; init; } }
///     public sealed record Response(Guid Id, string Name);
///
///     public ValueTask&lt;Result&lt;ElarionCreated&lt;Response&gt;&gt;&gt; HandleAsync(Command command, CancellationToken ct) {
///         var client = new Response(Guid.CreateVersion7(), command.Name);
///         // ...persist...
///         return ValueTask.FromResult&lt;Result&lt;ElarionCreated&lt;Response&gt;&gt;&gt;(
///             new ElarionCreated&lt;Response&gt;(client) { Location = $"clients/{client.Id}" });
///     }
/// }
/// </code>
/// </example>
/// <typeparam name="T">The created resource's response type — the body of the <c>201</c> response.</typeparam>
public sealed class ElarionCreated<T> {
    /// <summary>Wraps <paramref name="value"/> as a created-resource response.</summary>
    /// <param name="value">The response body describing the created resource.</param>
    public ElarionCreated(T value) {
        Value = value;
    }

    /// <summary>The response body describing the created resource.</summary>
    public T Value { get; }

    /// <summary>
    /// The URI of the created resource, sent as the <c>Location</c> header (absolute, or relative to the
    /// request URI). Leave <c>null</c> when the resource has no addressable route; the response is then
    /// <c>201</c> without a <c>Location</c> header.
    /// </summary>
    public string? Location { get; init; }
}
