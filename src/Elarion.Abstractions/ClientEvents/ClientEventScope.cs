namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// The audience of a single client-event publish: everyone on the topic (<see cref="Global"/>), one user
/// (<see cref="User"/>), or the subscribers of an application-defined resource key (<see cref="Resource"/>).
/// </summary>
/// <remarks>
/// Scope is data computed at publish time (e.g. <c>ClientEventScope.Resource($"customer:{evt.CustomerId}")</c>)
/// — which is why audience routing is an argument and not an attribute. The <see langword="default"/> value is
/// <see cref="Global"/>.
/// </remarks>
public readonly record struct ClientEventScope {
    private ClientEventScope(ClientEventScopeKind kind, string? value) {
        Kind = kind;
        Value = value;
    }

    /// <summary>The audience kind.</summary>
    public ClientEventScopeKind Kind { get; }

    /// <summary>The user id (<see cref="ClientEventScopeKind.User"/>) or resource key
    /// (<see cref="ClientEventScopeKind.Resource"/>); <see langword="null"/> for <see cref="Global"/>.</summary>
    public string? Value { get; }

    /// <summary>Every subscriber of the topic.</summary>
    public static ClientEventScope Global { get; } = new(ClientEventScopeKind.Global, null);

    /// <summary>The subscribers authenticated as <paramref name="userId"/>.</summary>
    public static ClientEventScope User(string userId) {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        return new ClientEventScope(ClientEventScopeKind.User, userId);
    }

    /// <summary>The subscribers of the application-defined resource <paramref name="key"/>.</summary>
    public static ClientEventScope Resource(string key) {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return new ClientEventScope(ClientEventScopeKind.Resource, key);
    }
}
