namespace Elarion.Abstractions.Previews;

/// <summary>
/// A command that can be previewed: run fully, its outcome reported, and every database write rolled back. The
/// same handler is the preview and the commit, so the two cannot decide differently by construction; the
/// <see cref="PreviewFingerprint"/> of the preview the caller saw lets the commit refuse to act on a decision that
/// changed in between.
/// </summary>
/// <remarks>
/// <para>
/// The handler generator attaches the framework's preview decorator to every handler whose request implements this
/// interface and whose response is <c>Result&lt;TOutcome&gt;</c> with <c>TOutcome : IPreviewOutcome&lt;TOutcome&gt;</c>
/// (otherwise it reports <c>ELPREV001</c>). The decorator sits just inside idempotency and outside the application's
/// <c>[DecoratorList]</c>, so it owns the unit of work of a preview and of a fingerprinted commit:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Preview"/> <see langword="false"/> and no <see cref="PreviewFingerprint"/>: an ordinary command; the
/// decorator passes the call straight through.
/// </description></item>
/// <item><description>
/// <see cref="Preview"/> <see langword="true"/>: the command runs, its outcome is stamped with its fingerprint and
/// returned, and the unit of work is rolled back.
/// </description></item>
/// <item><description>
/// <see cref="PreviewFingerprint"/> set on a commit: the command runs and commits only when its outcome has the same
/// fingerprint; otherwise it rolls back and fails with <see cref="ErrorCodes.PreviewChanged"/>, carrying the new
/// outcome as the error's data.
/// </description></item>
/// </list>
/// <para>
/// A preview needs a unit of work that really rolls back (<see cref="Pipeline.IUnitOfWork.SupportsRollback"/>);
/// without one the decorator answers <see cref="ErrorCodes.PreviewUnsupported"/> and never runs the handler. A
/// rollback undoes database writes only: a handler that calls something outside the database checks
/// <see cref="Preview"/> and skips it.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Defaults make both members optional on the wire: an ordinary call sends neither.
/// public sealed record Command(Guid StatementId, bool Preview = false, string? PreviewFingerprint = null)
///     : IPreviewableRequest;
/// </code>
/// </example>
public interface IPreviewableRequest : ICommand {
    /// <summary>
    /// Whether to run the command as a preview: report its outcome and roll every database write back.
    /// </summary>
    bool Preview { get; }

    /// <summary>
    /// The fingerprint of the preview the caller saw (<see cref="IPreviewOutcome{TSelf}.PreviewFingerprint"/>), or
    /// <see langword="null"/> to commit without comparing. When set on a commit, the command commits only if its
    /// outcome has the same fingerprint.
    /// </summary>
    string? PreviewFingerprint { get; }
}
