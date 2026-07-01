namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// The JSON storage envelope for a replayable <c>Result&lt;T&gt;</c> outcome: a success value, or a stored
/// (definitive) failure. Used by the generated idempotency payload policy so replay reconstructs the exact
/// <c>Result&lt;T&gt;</c> — success or failure — the first request produced.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public sealed class StoredResult<T> {
    /// <summary>Whether the stored outcome is a success.</summary>
    public bool Ok { get; set; }

    /// <summary>The success value, when <see cref="Ok"/> is <see langword="true"/>.</summary>
    public T? Value { get; set; }

    /// <summary>The error, when <see cref="Ok"/> is <see langword="false"/>.</summary>
    public AppError? Error { get; set; }
}

/// <summary>The JSON storage envelope for a non-generic <see cref="Result"/> outcome (success, or a stored failure).</summary>
public sealed class StoredResult {
    /// <summary>Whether the stored outcome is a success.</summary>
    public bool Ok { get; set; }

    /// <summary>The error, when <see cref="Ok"/> is <see langword="false"/>.</summary>
    public AppError? Error { get; set; }
}
