namespace Elarion.Abstractions.Connections;

/// <summary>The outcome of an attempted anonymous-to-authenticated connection identity promotion.</summary>
public enum ClientConnectionPromotionStatus {
    /// <summary>The new identity was committed and promotion observers were notified.</summary>
    Promoted,

    /// <summary>The connection was no longer registered when promotion was attempted.</summary>
    ConnectionNotFound,

    /// <summary>The connection already has an authenticated identity or was promoted by a concurrent caller.</summary>
    AlreadyAuthenticated,
}
