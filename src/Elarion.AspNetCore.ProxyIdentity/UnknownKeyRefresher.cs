using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// <see cref="ProxyIdentityOptions.AwaitRefreshOnUnknownKey"/>: for a token naming a key id the current keys do not hold,
/// fetches the key endpoint once and lets the request wait for it. IdentityModel's <see cref="ConfigurationManager{T}"/>
/// refreshes such a configuration only in the background, so the request that discovers a rotation is refused; this
/// fetch runs beside it with the same retriever and the same issuer-bound validation, and the keys it fetched are offered
/// to validation by a key resolver for <see cref="ProxyIdentityOptions.RefreshInterval"/>, while the regular refresh
/// catches up. The fetch is single-flight and at most one per <see cref="ProxyIdentityOptions.RefreshInterval"/>, so
/// forged key ids cannot raise the load on the key endpoint above what IdentityModel already allows.
/// </summary>
internal sealed class UnknownKeyRefresher {
    private readonly Lock _lock = new();
    private readonly string _address;
    private readonly IConfigurationRetriever<OpenIdConnectConfiguration> _retriever;
    private readonly IDocumentRetriever _documents;
    private readonly IssuerBoundConfigurationValidator _validator;
    private readonly ProxyIdentityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private Task? _fetch;
    private long? _lastFetchStarted;
    private FetchedKeys? _fetched;

    public UnknownKeyRefresher(
        ProxyIdentityOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<UnknownKeyRefresher> logger) {
        (_address, _retriever, _documents) = ProxySigningKeys.Retrieval(options, httpClient);
        _validator = new IssuerBoundConfigurationValidator(options.Issuer!);
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The key id a token names in its header, or null when it names none or is not a JSON Web Token.</summary>
    public static string? KeyId(string token) {
        try {
            return new JsonWebToken(token).Kid is { Length: > 0 } kid ? kid : null;
        }
        catch (ArgumentException) {
            return null;
        }
    }

    /// <summary>
    /// Makes sure the keys a request validates with hold <paramref name="kid"/> when the endpoint has it: waits for a
    /// running fetch, or starts one unless one started within the refresh interval, for at most the configured timeout.
    /// </summary>
    public async Task EnsureKeyAsync(
        string kid,
        IConfigurationManager<OpenIdConnectConfiguration> current,
        CancellationToken cancellationToken) {
        if (FetchedKey(kid) is not null) return;

        OpenIdConnectConfiguration configuration;
        try {
            configuration = await current.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) {
            // No configuration yet (the first fetch failed): JwtBearer reports it; the first fetch is not ours to repeat.
            return;
        }
        if (Holds(configuration.SigningKeys, kid)) return;

        Task fetch;
        lock (_lock) {
            if (_fetch is { IsCompleted: false } running) {
                fetch = running;
            }
            else {
                var now = _timeProvider.GetTimestamp();
                if (_lastFetchStarted is { } last && _timeProvider.GetElapsedTime(last, now) < _options.RefreshInterval) {
                    return;
                }
                _lastFetchStarted = now;
                // The fetch outlives the request that started it: a request that stops waiting leaves it to the next.
                fetch = _fetch = FetchAsync();
            }
        }

        try {
            await fetch.WaitAsync(_options.UnknownKeyRefreshTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) {
            _logger.LogWarning(
                "The proxy token names the unknown signing key {KeyId}; the key endpoint did not answer within {Timeout}.",
                kid, _options.UnknownKeyRefreshTimeout);
        }
        // Whatever the fetch found, the regular refresh should catch up (rate-limited by IdentityModel itself).
        current.RequestRefresh();
    }

    /// <summary>
    /// The signing keys a token naming <paramref name="kid"/> validates with: those of the current configuration, else
    /// those of a fetch this refresher made within the refresh interval. Null when the token names no key id, so
    /// validation tries every key as it would without the resolver.
    /// </summary>
    public IEnumerable<SecurityKey>? ResolveKeys(string? kid, BaseConfiguration? configuration) {
        if (string.IsNullOrEmpty(kid)) return null;

        if (configuration is not null) {
            var known = configuration.SigningKeys.Where(key => key.KeyId == kid).ToList();
            if (known.Count > 0) return known;
        }
        return FetchedKey(kid) is { } fetched ? [fetched] : null;
    }

    private SecurityKey? FetchedKey(string kid) {
        var fetched = Volatile.Read(ref _fetched);
        if (fetched is null || _timeProvider.GetElapsedTime(fetched.Timestamp) >= _options.RefreshInterval) return null;
        return fetched.Keys.FirstOrDefault(key => key.KeyId == kid);
    }

    private static bool Holds(IEnumerable<SecurityKey> keys, string kid) {
        return keys.Any(key => key.KeyId == kid);
    }

    private async Task FetchAsync() {
        // Off the caller's stack, so a synchronously completing retriever cannot run inside the lock.
        await Task.Yield();
        try {
            var configuration = await _retriever.GetConfigurationAsync(_address, _documents, CancellationToken.None)
                .ConfigureAwait(false);
            var validation = _validator.Validate(configuration);
            if (!validation.Succeeded) {
                _logger.LogWarning("The signing-key fetch for an unknown key was refused: {Reason}", validation.ErrorMessage);
                return;
            }
            Volatile.Write(ref _fetched, new FetchedKeys([.. configuration.SigningKeys], _timeProvider.GetTimestamp()));
        }
        catch (Exception exception) {
            _logger.LogWarning(exception, "The signing-key fetch for an unknown key failed; the current keys stay.");
        }
    }

    private sealed record FetchedKeys(IReadOnlyList<SecurityKey> Keys, long Timestamp);
}
