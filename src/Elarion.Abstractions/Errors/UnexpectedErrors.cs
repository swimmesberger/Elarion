using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Abstractions;

/// <summary>
/// The context of an exception that no handler turned into an <see cref="AppError"/>, handed to the
/// <see cref="UnexpectedErrorOptions"/> hooks by the transport that caught it.
/// </summary>
public sealed record UnexpectedErrorContext {
    /// <summary>The exception the transport caught.</summary>
    public required Exception Exception { get; init; }

    /// <summary>The transport that caught it; one of the <see cref="UnexpectedErrorTransports"/> names.</summary>
    public required string Transport { get; init; }

    /// <summary>
    /// The operation being served — the JSON-RPC method, the MCP tool's operation, or the HTTP endpoint name — or
    /// <see langword="null"/> when the failure happened before an operation was resolved.
    /// </summary>
    public string? Operation { get; init; }

    /// <summary>The call's scoped services, so a hook can resolve a localizer or the current user.</summary>
    public required IServiceProvider Services { get; init; }
}

/// <summary>The transport names an <see cref="UnexpectedErrorContext"/> carries.</summary>
public static class UnexpectedErrorTransports {
    /// <summary>The JSON-RPC endpoint.</summary>
    public const string JsonRpc = "jsonrpc";

    /// <summary>An MCP tool call.</summary>
    public const string Mcp = "mcp";

    /// <summary>A generated <c>[HttpEndpoint]</c> route.</summary>
    public const string Http = "http";
}

/// <summary>The answer a transport gives for an unexpected exception: the wire message and the support reference.</summary>
public sealed record UnexpectedError {
    /// <summary>The message the caller receives. Never the exception's message unless a hook chooses to expose it.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The reference the caller can hand to support, or <see langword="null"/> for none. It travels in its own wire
    /// field (<c>error.data.reference</c> on JSON-RPC and MCP, the <c>reference</c> ProblemDetails member on HTTP),
    /// never inside <see cref="Message"/>, and the transport logs it with the exception so the two can be matched.
    /// </summary>
    public string? Reference { get; init; }
}

/// <summary>
/// Configures the one answer every Elarion transport — JSON-RPC, MCP, and generated <c>[HttpEndpoint]</c> routes —
/// gives for an exception that escaped the handler pipeline: the stable code <see cref="ErrorCodes.Internal"/>, the
/// <see cref="Message"/>, and the <see cref="Reference"/>. The transports catch outside every decorator, so an
/// exception thrown by the authorization, validation, audit or observability decorator gets the same answer as one
/// thrown by the handler, and no application decorator, exception handler, or pipeline-order test is needed for it.
/// </summary>
/// <remarks>
/// Register it with <c>services.ConfigureElarionUnexpectedErrors(o =&gt; …)</c>. Unset hooks keep their defaults:
/// the message <see cref="DefaultMessage"/> and, as the reference, the current trace id
/// (<see cref="Activity.TraceId"/>), so a support reference finds the request's trace and log lines; with no
/// active trace, a fresh identifier that the transport writes to its error log.
/// </remarks>
/// <example>
/// <code>
/// builder.Services.ConfigureElarionUnexpectedErrors(o =&gt;
///     o.Message = _ =&gt; "Something went wrong. Please contact support and quote the reference.");
/// </code>
/// </example>
public sealed class UnexpectedErrorOptions {
    /// <summary>The message used when <see cref="Message"/> is not set.</summary>
    public const string DefaultMessage = "Internal error";

    /// <summary>Builds the caller-facing message. Defaults to <see cref="DefaultMessage"/>.</summary>
    public Func<UnexpectedErrorContext, string>? Message { get; set; }

    /// <summary>
    /// Builds the support reference; returning <see langword="null"/> sends none. Defaults to the current trace id,
    /// or a fresh identifier when no trace is active.
    /// </summary>
    public Func<UnexpectedErrorContext, string?>? Reference { get; set; }

    /// <summary>
    /// For generated <c>[HttpEndpoint]</c> routes only: returning <see langword="true"/> lets the exception propagate to
    /// the host's exception handling — <c>UseExceptionHandler</c>, an <c>IExceptionHandler</c> that maps it to its own
    /// status, the developer exception page — instead of answering it. Defaults to answering every exception.
    /// JSON-RPC and MCP always answer: a batch entry or a tool call has no host handler to fall back to.
    /// </summary>
    /// <example>
    /// <code>
    /// // The developer exception page in Development; a concurrency conflict mapped by the host's IExceptionHandler.
    /// o.LeaveToHost = c =&gt; builder.Environment.IsDevelopment() || c.Exception is DbUpdateConcurrencyException;
    /// </code>
    /// </example>
    public Func<UnexpectedErrorContext, bool>? LeaveToHost { get; set; }

    /// <summary>
    /// Describes <paramref name="context"/> with the options registered in its <see cref="UnexpectedErrorContext.Services"/>
    /// (or the defaults when none are registered). Called by the transports; a hook that throws — or a configuration
    /// that throws while the options are built — falls back to the default for that part, because the error path must
    /// always answer.
    /// </summary>
    public static UnexpectedError Describe(UnexpectedErrorContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var options = Resolve(context);
        return new UnexpectedError {
            Message = Invoke(options?.Message, context, static () => DefaultMessage) ?? DefaultMessage,
            Reference = Invoke(options?.Reference, context, DefaultReference)
        };
    }

    /// <summary>
    /// Whether the HTTP transport leaves <paramref name="context"/>'s exception to the host (see <see cref="LeaveToHost"/>).
    /// A hook that throws answers <see langword="false"/>: the endpoint then answers the exception itself.
    /// </summary>
    public static bool LeavesToHost(UnexpectedErrorContext context) {
        ArgumentNullException.ThrowIfNull(context);
        return Invoke(Resolve(context)?.LeaveToHost, context, static () => false);
    }

    private static UnexpectedErrorOptions? Resolve(UnexpectedErrorContext context) {
        try {
            return context.Services.GetService<UnexpectedErrorOptions>();
        }
        catch (Exception) {
            return null;
        }
    }

    // A missing hook and a throwing hook both yield the default; a hook that returns null (a reference it chose not
    // to send) is respected.
    private static T Invoke<T>(Func<UnexpectedErrorContext, T>? hook, UnexpectedErrorContext context,
        Func<T> fallback) {
        if (hook is null) return fallback();

        try {
            return hook(context);
        }
        catch (Exception) {
            return fallback();
        }
    }

    // The trace id ties the reference to the request's spans and log lines without a second correlation scheme; the
    // fallback identifier is only ever matched against the transport's own error log line.
    private static string? DefaultReference() {
        return Activity.Current is { } activity && activity.TraceId != default
            ? activity.TraceId.ToHexString()
            : Guid.CreateVersion7().ToString("N");
    }
}

/// <summary>Registration for <see cref="UnexpectedErrorOptions"/>.</summary>
public static class UnexpectedErrorServiceCollectionExtensions {
    /// <summary>
    /// Configures the answer every transport gives for an unexpected exception (see
    /// <see cref="UnexpectedErrorOptions"/>). Calls accumulate in registration order, so a later call overrides only
    /// the hooks it sets.
    /// </summary>
    public static IServiceCollection ConfigureElarionUnexpectedErrors(
        this IServiceCollection services,
        Action<UnexpectedErrorOptions> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddSingleton(new UnexpectedErrorConfigurator(configure));
        services.TryAddSingleton(static sp => {
            var options = new UnexpectedErrorOptions();
            foreach (var configurator in sp.GetServices<UnexpectedErrorConfigurator>())
                configurator.Configure(options);

            return options;
        });

        return services;
    }

    // One registration per ConfigureElarionUnexpectedErrors call, applied in order when the options materialize.
    private sealed class UnexpectedErrorConfigurator(Action<UnexpectedErrorOptions> configure) {
        public void Configure(UnexpectedErrorOptions options) {
            configure(options);
        }
    }
}
