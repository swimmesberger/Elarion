using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elarion.WebDav;

/// <summary>
/// A registered collection source of a protocol (a calendar source, an address-book source): its name, its query, the
/// handler service it needs, and how to invoke it as the current user (ADR-0111 section 3, ADR-0113 section 1).
/// </summary>
/// <typeparam name="TListing">The listing the source's handler returns, such as <c>CalDavSourceListing</c>.</typeparam>
internal sealed class DavSourceRegistration<TListing>(
    string name,
    Type queryType,
    Type handlerServiceType,
    Func<IServiceProvider, CancellationToken, ValueTask<Result<TListing>>> invoke) {
    public string Name { get; } = name;

    public Type QueryType { get; } = queryType;

    public Type HandlerServiceType { get; } = handlerServiceType;

    public Func<IServiceProvider, CancellationToken, ValueTask<Result<TListing>>> Invoke { get; } = invoke;
}

/// <summary>The registered sources of one protocol, by name.</summary>
internal sealed class DavSourceRegistry<TListing> {
    private readonly Dictionary<string, DavSourceRegistration<TListing>> _sources = new(StringComparer.Ordinal);

    public DavSourceRegistry(IEnumerable<DavSourceRegistration<TListing>> registrations, DavSourceKindOf<TListing> kindOf) {
        var kind = kindOf.Kind;
        foreach (var registration in registrations)
            if (!_sources.TryAdd(registration.Name, registration))
                throw new InvalidOperationException($"Two {kind.Noun}s are named '{registration.Name}'; source names must be unique.");
    }

    public IReadOnlyCollection<DavSourceRegistration<TListing>> Sources => _sources.Values;

    public bool Contains(string name) => _sources.ContainsKey(name);
}

/// <summary>What a protocol calls its sources in messages, such as <c>calendar source</c>.</summary>
internal sealed record DavSourceKind(string Noun, string RegistrationMethod);

/// <summary>The <see cref="DavSourceKind"/> of the protocol whose sources return <typeparamref name="TListing"/>, as a service.</summary>
internal sealed record DavSourceKindOf<TListing>(DavSourceKind Kind);

/// <summary>The registration and invocation of collection sources, shared by CalDAV and CardDAV.</summary>
internal static class DavSources {
    /// <summary>The longest source name or key: 64 characters.</summary>
    public const int MaxSegmentLength = 64;

    /// <summary>Whether <paramref name="value"/> is 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static bool IsValidSegment(string? value) {
        if (string.IsNullOrEmpty(value) || value.Length > MaxSegmentLength) return false;
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        return true;
    }

    /// <summary>
    /// Registers the source <typeparamref name="TQuery"/> named <paramref name="name"/>, failing for an invalid or a
    /// duplicate name, and the registry and start check of its protocol.
    /// </summary>
    public static void Register<TQuery, TListing, TOptions>(IServiceCollection services, string name, DavSourceKind kind)
        where TQuery : IQuery<TQuery, TListing>, new()
        where TOptions : class {
        if (!IsValidSegment(name))
            throw new InvalidOperationException(
                $"The {kind.Noun} {typeof(TQuery).Name} is named '{name}'; a source name must be 1 to 64 ASCII letters, digits, '-' or '_'.");
        foreach (var descriptor in services)
            if (descriptor.ServiceType == typeof(DavSourceRegistration<TListing>)
                && descriptor.ImplementationInstance is DavSourceRegistration<TListing> existing && existing.Name == name)
                throw new InvalidOperationException($"Two {kind.Noun}s are named '{name}'; {typeof(TQuery).Name} must use another name.");

        services.AddSingleton(new DavSourceRegistration<TListing>(name, typeof(TQuery), typeof(IHandler<TQuery, Result<TListing>>),
            static (provider, cancellationToken) =>
                provider.GetRequiredService<IHandler<TQuery, Result<TListing>>>().HandleAsync(new TQuery(), cancellationToken)));
    }

    /// <summary>
    /// Adds the registry of <typeparamref name="TListing"/> sources and the start check that each has a handler; the
    /// caller makes <typeparamref name="TOptions"/> validate on start.
    /// </summary>
    public static void AddRegistry<TListing, TOptions>(IServiceCollection services, DavSourceKind kind) where TOptions : class {
        services.TryAddSingleton(new DavSourceKindOf<TListing>(kind));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, DavSourceHandlerValidator<TListing, TOptions>>());
        services.TryAddSingleton<DavSourceRegistry<TListing>>();
    }

    /// <summary>
    /// Invokes <paramref name="source"/> in the request's scope. A source the user may not use, behind a closed feature
    /// gate, or of a disabled module answers <see langword="null"/>: it shows no collections (spec 0006 FR-011, FR-012);
    /// any other failure is the application's and throws.
    /// </summary>
    public static async ValueTask<TListing?> InvokeAsync<TListing>(DavSourceRegistration<TListing> source, DavSourceKind kind, IServiceProvider services,
        CancellationToken cancellationToken) where TListing : class {
        var result = await source.Invoke(services, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess) return result.Value;
        if (result.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden or ErrorKind.Unauthorized) return null;
        throw new InvalidOperationException($"The {kind.Noun} '{source.Name}' failed: {result.Error.Code}: {result.Error.Message}");
    }

    /// <summary>Checks a key a source returned: valid and unique within the source.</summary>
    public static void CheckKey(string sourceName, string key, HashSet<string> seen, DavSourceKind kind) {
        if (!IsValidSegment(key))
            throw new InvalidOperationException(
                $"The {kind.Noun} '{sourceName}' returned the key '{key}'; a key must be 1 to 64 ASCII letters, digits, '-' or '_'.");
        if (!seen.Add(key))
            throw new InvalidOperationException($"The {kind.Noun} '{sourceName}' returned the key '{key}' twice.");
    }

    /// <summary>
    /// The URL segment of a collection a source returned: the explicit <paramref name="segment"/>, checked, or
    /// <c>{source}.{key}</c>; unique among every collection of the protocol the user sees (<paramref name="seen"/>).
    /// </summary>
    public static string SegmentOf(string sourceName, string key, string? segment, HashSet<string> seen, DavSourceKind kind) {
        if (segment is not null && !DavCollection.IsValidSegment(segment))
            throw new InvalidOperationException(
                $"The {kind.Noun} '{sourceName}' returned the URL segment '{segment}' for '{key}'; a segment must be 1 to "
                + $"{DavCollection.MaxSegmentLength} ASCII letters, digits, '-', '_', '.', '~' or '@', and not '.' or '..'.");
        var actual = segment ?? sourceName + "." + key;
        if (!seen.Add(actual))
            throw new InvalidOperationException(
                $"Two collections of the {kind.Noun}s have the URL segment '{actual}' (the second from '{sourceName}', key '{key}'); "
                + "segments must be unique within a home.");
        return actual;
    }
}

/// <summary>
/// Fails the host's start when a registered source has no handler, instead of the first DAV request answering 500
/// (spec 0006 FR-013, spec 0007 FR-014): the handler is resolved only per request, so nothing else would notice.
/// </summary>
internal sealed class DavSourceHandlerValidator<TListing, TOptions>(
    IEnumerable<DavSourceRegistration<TListing>> sources,
    IServiceProviderIsService services,
    DavSourceKindOf<TListing> kindOf) : IValidateOptions<TOptions> where TOptions : class {
    public ValidateOptionsResult Validate(string? name, TOptions options) {
        var kind = kindOf.Kind;
        var listing = typeof(TListing).Name;
        var missing = sources.Where(source => !services.IsService(source.HandlerServiceType))
            .Select(source =>
                $"The {kind.Noun} '{source.Name}' ({source.QueryType.Name}) has no handler: no IHandler<{source.QueryType.Name}, "
                + $"Result<{listing}>> is registered. Add the handler class to the module that calls {kind.RegistrationMethod}, "
                + "or register it.")
            .ToList();
        return missing.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(missing);
    }
}
