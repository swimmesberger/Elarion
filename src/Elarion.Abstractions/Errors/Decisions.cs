using System.Text.Json;

namespace Elarion.Abstractions;

/// <summary>
/// A request whose handler may need the caller's decision before it runs (ADR-0101). The answers travel in one
/// member, <see cref="Decisions"/>, whatever the decision; the handler reads them with the
/// <see cref="DecisionExtensions"/> helpers and asks with <see cref="AppError.DecisionRequired"/> when one is missing.
/// </summary>
/// <example>
/// <code>
/// public sealed record Command : ICommand, IDecidingRequest {
///     public required Guid InvoiceId { get; init; }
///     public IReadOnlyList&lt;DecisionAnswer&gt;? Decisions { get; init; }
/// }
/// </code>
/// </example>
public interface IDecidingRequest {
    /// <summary>The caller's answers to the decisions the handler asked, or <see langword="null"/> before any.</summary>
    IReadOnlyList<DecisionAnswer>? Decisions { get; }
}

/// <summary>One answer to a decision: its <see cref="Code"/>, the chosen <see cref="Option"/>, and for a per-item decision
/// the <see cref="Item"/> it answers.</summary>
/// <remarks>
/// <see cref="Confirming(string)"/> and <see cref="Choosing{TOption}(string, TOption)"/> (and their per-item overloads)
/// build an answer in one line, with the option's wire name spelled as <see cref="DecisionExtensions"/> reads it.
/// </remarks>
/// <example>
/// <code>
/// var command = new CommitImport.Command {
///     Statement = statement,
///     Decisions = [
///         DecisionAnswer.Confirming(Codes.ClosedPeriod),
///         DecisionAnswer.Choosing(Codes.PossibleDuplicate, "line-3", DuplicateChoice.Skip)
///     ]
/// };
/// </code>
/// </example>
public sealed record DecisionAnswer {
    /// <summary>The one option of a confirmation (a decision declared without options).</summary>
    public const string Confirm = "confirm";

    /// <summary>An answer that confirms the whole-request decision <paramref name="code"/>.</summary>
    public static DecisionAnswer Confirming(string code) {
        ArgumentNullException.ThrowIfNull(code);
        return new DecisionAnswer { Code = code };
    }

    /// <summary>An answer that confirms <paramref name="item"/> of the per-item decision <paramref name="code"/>.</summary>
    public static DecisionAnswer Confirming(string code, string item) {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(item);
        return new DecisionAnswer { Code = code, Item = item };
    }

    /// <summary>
    /// An answer that chooses <paramref name="option"/> for the whole-request decision <paramref name="code"/>: the
    /// option's wire name is the enum member's name in camel case, which
    /// <see cref="DecisionExtensions.Decision{TOption}(IDecidingRequest, string)"/> reads back.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="option"/> is not a declared member of the enum.</exception>
    public static DecisionAnswer Choosing<TOption>(string code, TOption option) where TOption : struct, Enum {
        ArgumentNullException.ThrowIfNull(code);
        return new DecisionAnswer { Code = code, Option = OptionName(option) };
    }

    /// <summary>
    /// An answer that chooses <paramref name="option"/> for <paramref name="item"/> of the per-item decision
    /// <paramref name="code"/>, with the option's wire name spelled as for <see cref="Choosing{TOption}(string, TOption)"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="option"/> is not a declared member of the enum.</exception>
    public static DecisionAnswer Choosing<TOption>(string code, string item, TOption option) where TOption : struct, Enum {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(item);
        return new DecisionAnswer { Code = code, Item = item, Option = OptionName(option) };
    }

    // The wire name the schema exports for an option: the member name in System.Text.Json's camel case, as the
    // generator spells it. A value the enum does not declare (or a combination of flags) has no name.
    private static string OptionName<TOption>(TOption option) where TOption : struct, Enum {
        return Enum.GetName(option) is { } name
            ? JsonNamingPolicy.CamelCase.ConvertName(name)
            : throw new ArgumentOutOfRangeException(nameof(option), option,
                $"'{option}' is not a declared member of {typeof(TOption).Name}, so it is no option.");
    }

    /// <summary>The decision's code, as the handler declared it with <see cref="RequiresDecisionAttribute"/>.</summary>
    public required string Code { get; init; }

    /// <summary>The item this answer is for, for a per-item decision; <see langword="null"/> for the whole request.</summary>
    public string? Item { get; init; }

    /// <summary>
    /// The chosen option's wire name (the option enum member's name in camel case), or <see cref="Confirm"/> for a
    /// confirmation.
    /// </summary>
    public string Option { get; init; } = Confirm;
}

/// <summary>
/// Reads the caller's answers from an <see cref="IDecidingRequest"/>. An answer whose option is not one of the declared
/// options counts as no answer, so the handler simply asks again.
/// </summary>
/// <example>
/// <code>
/// // A confirmation for the whole request.
/// if (duplicate is not null &amp;&amp; !command.IsConfirmed(Codes.Duplicate))
///     return AppError.DecisionRequired(Codes.Duplicate, "A booking like this exists.", new Duplicate(duplicate.Id));
///
/// // A choice per item: refuse while any item the server found is unanswered, then act on each answer.
/// var unanswered = command.Unanswered(Codes.PossibleDuplicate, duplicates.Select(d =&gt; d.Key));
/// if (unanswered.Count &gt; 0)
///     return AppError.DecisionRequired(Codes.PossibleDuplicate, "Some lines look like existing bookings.",
///         new PossibleDuplicates(duplicates.Where(d =&gt; unanswered.Contains(d.Key)).ToList()));
/// foreach (var line in lines)
///     if (command.Decision&lt;DuplicateChoice&gt;(Codes.PossibleDuplicate, line.Key) != DuplicateChoice.Skip)
///         Import(line);
/// </code>
/// </example>
public static class DecisionExtensions {
    /// <summary>Whether the caller confirmed the whole-request decision <paramref name="code"/>.</summary>
    public static bool IsConfirmed(this IDecidingRequest request, string code) {
        return Find(request, code, null) is { } answer
               && string.Equals(answer.Option, DecisionAnswer.Confirm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the caller confirmed the per-item decision <paramref name="code"/> for <paramref name="item"/>.</summary>
    public static bool IsConfirmed(this IDecidingRequest request, string code, string item) {
        ArgumentNullException.ThrowIfNull(item);
        return Find(request, code, item) is { } answer
               && string.Equals(answer.Option, DecisionAnswer.Confirm, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The option the caller chose for the whole-request decision <paramref name="code"/>, or <see langword="null"/>
    /// when it has not answered (or answered with an option <typeparamref name="TOption"/> does not declare).
    /// </summary>
    public static TOption? Decision<TOption>(this IDecidingRequest request, string code) where TOption : struct, Enum {
        return Parse<TOption>(Find(request, code, null));
    }

    /// <summary>
    /// The option the caller chose for <paramref name="item"/> of the per-item decision <paramref name="code"/>, or
    /// <see langword="null"/> when it has not answered (or answered with an undeclared option).
    /// </summary>
    public static TOption? Decision<TOption>(this IDecidingRequest request, string code, string item)
        where TOption : struct, Enum {
        ArgumentNullException.ThrowIfNull(item);
        return Parse<TOption>(Find(request, code, item));
    }

    /// <summary>
    /// The <paramref name="items"/> the caller has not answered for the per-item decision <paramref name="code"/>, in
    /// their order: the items to ask about. Empty when every item has an answer.
    /// </summary>
    public static IReadOnlyList<string> Unanswered(this IDecidingRequest request, string code, IEnumerable<string> items) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(items);

        HashSet<string>? answered = null;
        if (request.Decisions is { Count: > 0 } decisions)
            foreach (var answer in decisions)
                if (answer.Item is not null && string.Equals(answer.Code, code, StringComparison.Ordinal))
                    (answered ??= new HashSet<string>(StringComparer.Ordinal)).Add(answer.Item);

        var unanswered = new List<string>();
        foreach (var item in items)
            if (answered is null || !answered.Contains(item))
                unanswered.Add(item);

        return unanswered;
    }

    // The last answer wins, so a client that appends a changed answer replaces the earlier one.
    private static DecisionAnswer? Find(IDecidingRequest request, string code, string? item) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(code);
        if (request.Decisions is not { Count: > 0 } decisions) return null;

        for (var i = decisions.Count - 1; i >= 0; i--) {
            var answer = decisions[i];
            if (string.Equals(answer.Code, code, StringComparison.Ordinal)
                && string.Equals(answer.Item, item, StringComparison.Ordinal))
                return answer;
        }

        return null;
    }

    // Option wire names are the enum member names in camel case; matching ignores case. A numeric string is never an
    // option (Enum.TryParse would accept it), and neither is an undeclared value.
    private static TOption? Parse<TOption>(DecisionAnswer? answer) where TOption : struct, Enum {
        if (answer is not { Option: { Length: > 0 } option } || !char.IsLetter(option[0])) return null;

        return Enum.TryParse<TOption>(option, true, out var value) && Enum.IsDefined(value) ? value : null;
    }
}
