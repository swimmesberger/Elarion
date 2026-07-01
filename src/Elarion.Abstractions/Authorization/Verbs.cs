namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The suggested verb vocabulary for <see cref="RequirePermissionAttribute"/>, mirroring the standard
/// Kubernetes-RBAC verbs. These are plain string constants — the verb is an <b>open</b> vocabulary, so a handler
/// may use one of these or any custom verb (e.g. <c>"approve"</c>, <c>"export"</c>). Using a shared constant keeps
/// the generated <c>ElarionPermissions.ByVerb</c> keys consistent across modules.
/// </summary>
public static class Verbs {
    /// <summary>Read or query a single resource.</summary>
    public const string Read = "read";

    /// <summary>List/enumerate resources.</summary>
    public const string List = "list";

    /// <summary>Create a resource.</summary>
    public const string Create = "create";

    /// <summary>Update or modify a resource.</summary>
    public const string Update = "update";

    /// <summary>Create or modify a resource (a coarse write covering create + update).</summary>
    public const string Write = "write";

    /// <summary>Delete a resource.</summary>
    public const string Delete = "delete";

    /// <summary>Administer a resource (configuration, sharing, lifecycle) beyond ordinary writes.</summary>
    public const string Manage = "manage";
}
