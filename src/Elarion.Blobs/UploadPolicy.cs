using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Identity;

namespace Elarion.Blobs;

/// <summary>
/// A named staging policy a client picks when it uploads — <c>?policy=mail.eml</c> on the direct endpoint, the
/// <c>policy</c> key of the tus <c>Upload-Metadata</c> — so one kind of upload may be larger than the endpoint's default
/// without raising the default for every upload, and may be checked against the record it is for before its bytes
/// arrive. Registered with <see cref="UploadPolicyServiceCollectionExtensions.AddElarionUploadPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// A limit the policy sets <b>replaces</b> the endpoint's (it may be larger or smaller); a limit it leaves
/// <see langword="null"/> is the endpoint's. The staged blob is the same pending upload as without a policy: the handler
/// that takes it still checks its own <c>[UploadLimits]</c>, so a handler that accepts the larger files declares the
/// larger limit, and one that does not refuses them.
/// </para>
/// <para>
/// <see cref="Authorize"/> runs after authentication and before the endpoint reads the body. It is a guard against
/// staging bytes for a record the caller cannot use, not the authorization of the record's change: the handler that
/// keeps the upload checks the record again, inside its transaction.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// services.AddElarionUploadPolicy("mail.eml", policy =&gt; {
///     policy.MaxBytes = 100L * 1024 * 1024;
///     policy.ContentTypes = ["message/rfc822", "application/octet-stream"];
///     // The draft exists and is the caller's, before 100 MB arrive.
///     policy.Authorize = static async (context, ct) =&gt;
///         Guid.TryParse(context.ResourceId, out var draftId)
///         &amp;&amp; await context.Services.GetRequiredService&lt;AppDbContext&gt;().Drafts
///             .AnyAsync(d =&gt; d.Id == draftId &amp;&amp; d.OwnerId == context.User.UserId, ct);
/// });
/// </code>
/// </example>
public sealed class UploadPolicy {
    /// <summary>
    /// The largest upload in bytes, replacing the endpoint's <c>MaxContentLength</c>/<c>MaxSize</c>;
    /// <see langword="null"/> keeps the endpoint's.
    /// </summary>
    public long? MaxBytes { get; set; }

    /// <summary>
    /// The accepted content types (case-insensitive, parameters ignored), replacing the endpoint's allow-list;
    /// <see langword="null"/> keeps the endpoint's, an empty list accepts any.
    /// </summary>
    public IReadOnlyCollection<string>? ContentTypes { get; set; }

    /// <summary>
    /// Whether the caller may stage an upload under this policy, asked before the body is read; <see langword="null"/>
    /// admits every authenticated caller. A refusal is answered as <c>404 Not Found</c>, so the record it names is not
    /// confirmed to exist.
    /// </summary>
    public Func<UploadPolicyContext, CancellationToken, ValueTask<bool>>? Authorize { get; set; }

    /// <summary>Asks <see cref="Authorize"/>, or admits when there is none.</summary>
    /// <param name="context">The staging request.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public ValueTask<bool> AllowsAsync(UploadPolicyContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        return Authorize is null ? ValueTask.FromResult(true) : Authorize(context, cancellationToken);
    }
}

/// <summary>The staging request an <see cref="UploadPolicy.Authorize"/> check is asked about.</summary>
public sealed class UploadPolicyContext {
    /// <summary>The policy's name.</summary>
    public required string Policy { get; init; }

    /// <summary>
    /// The record the upload is for, as the client named it (<c>resourceId</c> on the direct endpoint, the
    /// <c>resourceId</c> key of the tus metadata); <see langword="null"/> when it named none. Untrusted input.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>The authenticated caller.</summary>
    public required ICurrentUser User { get; init; }

    /// <summary>The request's service scope.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The length the client declared, when it declared one; the endpoint enforces the limit itself.</summary>
    public long? Length { get; init; }
}

/// <summary>
/// The registered <see cref="UploadPolicy"/> instances by name, which the upload endpoints resolve the policy a request
/// names from. A protocol adapter of its own resolves policies the same way.
/// </summary>
public sealed class UploadPolicies {
    private readonly Dictionary<string, UploadPolicy> _policies = new(StringComparer.Ordinal);

    /// <summary>The registered policy names.</summary>
    public IReadOnlyCollection<string> Names => _policies.Keys;

    /// <summary>The policy named <paramref name="name"/>, if one is registered (names compare ordinally).</summary>
    public bool TryGet(string name, [NotNullWhen(true)] out UploadPolicy? policy) {
        ArgumentNullException.ThrowIfNull(name);
        return _policies.TryGetValue(name, out policy);
    }

    internal UploadPolicy GetOrAdd(string name) {
        if (!_policies.TryGetValue(name, out var policy)) _policies[name] = policy = new UploadPolicy();
        return policy;
    }
}
