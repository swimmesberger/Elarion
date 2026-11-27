namespace Elarion.AppPasswords.EntityFrameworkCore;

/// <summary>
/// The persisted row behind the EF <see cref="IAppPasswordStore"/>: one app password, holding the hash of its secret,
/// never the secret. Revoking deletes the row.
/// </summary>
public sealed class AppPasswordEntity {
    /// <summary>The app password's id (a version 7 GUID; the primary key).</summary>
    public Guid Id { get; set; }

    /// <summary>The id of the user it belongs to, as <c>ICurrentUser.UserId</c>.</summary>
    public required string UserId { get; set; }

    /// <summary>The name the user gave it.</summary>
    public required string Name { get; set; }

    /// <summary>The <see cref="AppPasswordScope.Value"/>, such as <c>dav</c>.</summary>
    public required string Scope { get; set; }

    /// <summary>The hash of the secret, as <see cref="AppPasswordSecrets.ComputeHash"/> computes it (unique).</summary>
    public required string Hash { get; set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When it was last used, written at most once per <see cref="AppPasswordOptions.LastUseInterval"/>.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}
