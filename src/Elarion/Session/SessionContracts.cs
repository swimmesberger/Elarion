namespace Elarion.Session;

/// <summary>
/// The request for the client-capability bootstrap. It carries no parameters — the snapshot is derived entirely from
/// the ambient current user and the deployment manifest — but is a distinct type so it routes on the named bus and
/// over REST like any other operation.
/// </summary>
public sealed record SessionRequest;

/// <summary>
/// The client-capability snapshot for the current user and deployment: which modules are enabled, which exposed
/// flags/variants resolve to what, and the user's raw grants. The frontend reflects this (hides/adapts UI); it is a
/// <b>read-only UX projection, not an enforcement boundary</b> — the real gate is the handler's authorization. See
/// <c>ADR-0030</c> and the <c>client-capabilities</c> concept doc.
/// </summary>
public sealed record SessionResponse {
    /// <summary>The current user's identity and raw grants (roles + permissions).</summary>
    public required SessionUser User { get; init; }

    /// <summary>Each module's enabled state (deployment-scoped), keyed by module name.</summary>
    public required IReadOnlyDictionary<string, bool> Modules { get; init; }

    /// <summary>Each exposed boolean flag's value for the current user, keyed by flag name.</summary>
    public required IReadOnlyDictionary<string, bool> Flags { get; init; }

    /// <summary>The allocated variant for each exposed variant flag, keyed by flag name (only flags that resolved a variant).</summary>
    public required IReadOnlyDictionary<string, string> Variants { get; init; }
}

/// <summary>The current user's identity and raw grants, as projected into the session snapshot.</summary>
public sealed record SessionUser {
    /// <summary>The user identifier (empty for an anonymous caller).</summary>
    public required string Id { get; init; }

    /// <summary>The user's email, if available.</summary>
    public string? Email { get; init; }

    /// <summary>Whether the caller is authenticated.</summary>
    public required bool IsAuthenticated { get; init; }

    /// <summary>The user's roles.</summary>
    public required IReadOnlyList<string> Roles { get; init; }

    /// <summary>The user's permission grants (the values of the configured permission claim).</summary>
    public required IReadOnlyList<string> Permissions { get; init; }
}
