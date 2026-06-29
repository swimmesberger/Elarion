namespace Elarion.Abstractions.Authorization;

/// <summary>
/// A coarse classification of what a permission lets a principal do, declared on
/// <see cref="RequirePermissionAttribute"/>. It is an optional hint that drives the generated
/// <c>ElarionPermissions.ByKind</c> grouping, so role policy can grant, say, every read permission without
/// relying on a string-suffix convention (<c>p.EndsWith(".read")</c>). It does <b>not</b> affect the
/// authorization decision — enforcement is on the permission string alone.
/// </summary>
public enum PermissionKind {
    /// <summary>No classification given (the default). Grouped under its own bucket.</summary>
    Unspecified = 0,

    /// <summary>Reads/queries data.</summary>
    Read,

    /// <summary>Creates or mutates data.</summary>
    Write,

    /// <summary>Deletes data.</summary>
    Delete,

    /// <summary>Administers a resource (configuration, sharing, lifecycle) beyond ordinary writes.</summary>
    Manage,
}
