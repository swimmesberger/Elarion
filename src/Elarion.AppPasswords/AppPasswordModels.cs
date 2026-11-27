namespace Elarion.AppPasswords;

/// <summary>An app password as listed: everything but the secret, which is never readable after it was issued.</summary>
public sealed record AppPasswordInfo {
    /// <summary>The app password's id (a version 7 GUID).</summary>
    public required Guid Id { get; init; }

    /// <summary>The id of the user it belongs to, as <c>ICurrentUser.UserId</c>.</summary>
    public required string UserId { get; init; }

    /// <summary>The name the user gave it, such as <c>iPhone</c>.</summary>
    public required string Name { get; init; }

    /// <summary>What it may be used for.</summary>
    public required AppPasswordScope Scope { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was last used, to within <see cref="AppPasswordOptions.LastUseInterval"/>; null when never.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
/// A newly created app password with its secret. The secret is in this value only: show it to the user once, and do
/// not log or store it.
/// </summary>
public sealed record AppPasswordIssued {
    /// <summary>The app password as it will be listed.</summary>
    public required AppPasswordInfo Password { get; init; }

    /// <summary>The secret: the configured prefix and 256 random bits, base64url without padding.</summary>
    public required string Secret { get; init; }

    /// <summary>Prints the app password with the secret masked, so the secret cannot reach a log this way.</summary>
    public override string ToString() {
        return $"AppPasswordIssued {{ Password = {Password}, Secret = *** }}";
    }
}

/// <summary>
/// The outcome of a successful credential check: whose credential it is, what it may be used for, and its id. The
/// default check, <see cref="IAppPasswords"/>, returns it for Elarion's store; an application that checks credentials
/// of its own returns one for its credential.
/// </summary>
public sealed record AppPasswordMatch {
    /// <summary>The credential's id; the value of the <see cref="AppPasswordClaimTypes.Id"/> claim.</summary>
    public required string Id { get; init; }

    /// <summary>The id of the user the credential belongs to, as <c>ICurrentUser.UserId</c>.</summary>
    public required string UserId { get; init; }

    /// <summary>The credential's scope; the value of the <see cref="AppPasswordClaimTypes.Scope"/> claim.</summary>
    public required AppPasswordScope Scope { get; init; }
}

/// <summary>An app password as an <see cref="IAppPasswordStore"/> keeps it: its listing data and the hash of its secret.</summary>
public sealed record AppPasswordRecord {
    /// <summary>The app password's id (a version 7 GUID).</summary>
    public required Guid Id { get; init; }

    /// <summary>The id of the user it belongs to.</summary>
    public required string UserId { get; init; }

    /// <summary>The name the user gave it.</summary>
    public required string Name { get; init; }

    /// <summary>What it may be used for.</summary>
    public required AppPasswordScope Scope { get; init; }

    /// <summary>The hash of the secret, as <see cref="AppPasswordSecrets.ComputeHash"/> computes it.</summary>
    public required string Hash { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was last used; null when never.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>The record without its hash.</summary>
    public AppPasswordInfo ToInfo() {
        return new AppPasswordInfo {
            Id = Id, UserId = UserId, Name = Name, Scope = Scope, CreatedAt = CreatedAt, LastUsedAt = LastUsedAt,
        };
    }
}
