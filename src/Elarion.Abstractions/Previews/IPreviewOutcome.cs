namespace Elarion.Abstractions.Previews;

/// <summary>
/// The success value of a previewable command (<see cref="IPreviewableRequest"/>): the decision the command makes,
/// stamped with its fingerprint so the caller can send it back with the commit.
/// </summary>
/// <remarks>
/// <para>
/// The fingerprint is the SHA-256 (base64url) of the outcome's canonical JSON with
/// <see cref="PreviewFingerprint"/> cleared, so it changes exactly when the decision changes. The outcome must
/// therefore be deterministic for the same decision: no freshly minted ids, timestamps or random values, collections in
/// a stable order, and nothing that depends on <see cref="IPreviewableRequest.Preview"/> itself.
/// </para>
/// <para>
/// <see cref="WithPreviewFingerprint"/> lets the framework stamp the fingerprint without mutation or reflection; a
/// record implements it as <c>this with { PreviewFingerprint = fingerprint }</c>. The outcome type is serialized with
/// the canonical JSON options, so it must be registered in a source-generated context like any response type.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The implementing outcome type itself.</typeparam>
/// <example>
/// <code>
/// public sealed record Outcome(IReadOnlyList&lt;Row&gt; Rows, int NewCount, int HeldBackCount)
///     : IPreviewOutcome&lt;Outcome&gt; {
///     public string? PreviewFingerprint { get; init; }
///
///     public Outcome WithPreviewFingerprint(string? fingerprint) =&gt;
///         this with { PreviewFingerprint = fingerprint };
/// }
/// </code>
/// </example>
public interface IPreviewOutcome<TSelf> where TSelf : IPreviewOutcome<TSelf> {
    /// <summary>
    /// The fingerprint of this outcome, set by the framework on every previewed or fingerprinted call;
    /// <see langword="null"/> on an ordinary call. Send it back as <see cref="IPreviewableRequest.PreviewFingerprint"/>
    /// to commit exactly what was previewed.
    /// </summary>
    string? PreviewFingerprint { get; }

    /// <summary>Returns a copy of this outcome carrying <paramref name="fingerprint"/>.</summary>
    TSelf WithPreviewFingerprint(string? fingerprint);
}
