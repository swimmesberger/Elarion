namespace Elarion.Abstractions.Authorization;

/// <summary>
/// The action a caller wants to perform on a resource (read, create, update, delete, or any
/// application-defined verb such as <c>"export"</c>). Modeled as an <b>open value</b> — a thin wrapper over a
/// string name with well-known statics — rather than a closed enum, so an application can introduce its own
/// operations without a framework contract change (mirroring how <c>SettingsScope</c> is an open value).
/// </summary>
/// <remarks>
/// <see langword="default"/>(<see cref="ResourceOperation"/>) has a <see langword="null"/>
/// <see cref="Name"/> and is <b>not</b> a valid operation; construct one through the constructor or a static.
/// Authorizers should treat an unrecognized operation as the most restrictive (fail-safe) case.
/// </remarks>
public readonly record struct ResourceOperation {
    /// <summary>Initializes a new <see cref="ResourceOperation"/> with the given non-empty <paramref name="name"/>.</summary>
    /// <param name="name">The operation name (case-sensitive); compared by ordinal equality.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or whitespace.</exception>
    public ResourceOperation(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Resource operation name must be non-empty.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The operation name.</summary>
    public string Name { get; }

    /// <summary>The conventional read operation (<c>"read"</c>).</summary>
    public static ResourceOperation Read { get; } = new("read");

    /// <summary>The conventional create operation (<c>"create"</c>).</summary>
    public static ResourceOperation Create { get; } = new("create");

    /// <summary>The conventional update operation (<c>"update"</c>).</summary>
    public static ResourceOperation Update { get; } = new("update");

    /// <summary>The conventional delete operation (<c>"delete"</c>).</summary>
    public static ResourceOperation Delete { get; } = new("delete");

    /// <inheritdoc />
    public override string ToString() => Name;
}
