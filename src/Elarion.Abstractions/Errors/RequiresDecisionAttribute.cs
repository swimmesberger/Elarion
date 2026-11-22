namespace Elarion.Abstractions;

/// <summary>
/// Declares a decision the handler may need from its caller before it runs (ADR-0101): a stable
/// <see cref="Code"/>, the <see cref="Options"/> the caller picks from, whether it is answered once for the request or
/// <see cref="PerItem"/>, and the <see cref="DataType"/> the question carries. The handler asks with
/// <see cref="AppError.DecisionRequired"/>; the request implements <see cref="IDecidingRequest"/> to carry the answers.
/// </summary>
/// <remarks>
/// <para>
/// A decision is part of the operation's error contract, like a <see cref="ProducesErrorAttribute"/> declaration of
/// <see cref="ErrorKind.DecisionRequired"/>: the schema exports the code with its options, and the generated client's
/// <c>withDecisions</c> helper asks the application's dialog and resends with the answer.
/// </para>
/// <para>
/// <see cref="Options"/> is an enum whose members are the choices; their wire names are the member names in camel
/// case (<c>OnlyHere</c> → <c>"onlyHere"</c>). Without it the decision is a confirmation whose one option is
/// <see cref="DecisionAnswer.Confirm"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public enum DuplicateChoice { Import, Skip }
///
/// [Handler]
/// [RequiresDecision("banking.possible_duplicate", Options = typeof(DuplicateChoice), PerItem = true,
///     DataType = typeof(PossibleDuplicates))]
/// public sealed class CommitImport : IHandler&lt;CommitImport.Command, Result&lt;ImportOutcome&gt;&gt; {
///     public sealed record Command : ICommand, IDecidingRequest {
///         public required UploadedFile Statement { get; init; }
///         public IReadOnlyList&lt;DecisionAnswer&gt;? Decisions { get; init; }
///     }
///     …
/// }
/// </code>
/// </example>
/// <param name="code">The decision's stable code (see <see cref="ErrorCodes"/> for the format).</param>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequiresDecisionAttribute(string code) : Attribute {
    /// <summary>The decision's stable code; the code of the <see cref="AppError.DecisionRequired"/> that asks it.</summary>
    public string Code { get; } = code;

    /// <summary>
    /// The enum whose members are the choices, or <see langword="null"/> for a confirmation (one option,
    /// <see cref="DecisionAnswer.Confirm"/>).
    /// </summary>
    public Type? Options { get; init; }

    /// <summary>
    /// Whether the caller answers per item (each answer names an <see cref="DecisionAnswer.Item"/> key the question's
    /// data listed) instead of once for the whole request.
    /// </summary>
    public bool PerItem { get; init; }

    /// <summary>The payload type the question carries as <see cref="AppError.Data"/>, or <see langword="null"/>.</summary>
    public Type? DataType { get; init; }
}
