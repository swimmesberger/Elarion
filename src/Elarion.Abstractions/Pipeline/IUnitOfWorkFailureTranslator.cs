namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// Maps an exception that ends a framework-owned unit of work — thrown by the handler (a <c>SaveChanges</c> that hit a
/// unique index) or by the commit itself (a <c>DEFERRABLE INITIALLY DEFERRED</c> constraint checked at
/// <c>COMMIT</c>) — to the <see cref="AppError"/> the caller should get instead of an unexpected error.
/// </summary>
/// <remarks>
/// <para>
/// <c>TransactionDecorator</c> asks every registered translator, in registration order, once the exception has ended
/// the unit of work; the first non-<see langword="null"/> answer becomes the command's failed result and the unit of
/// work rolls back. When none answers, or the command's response is not a <see cref="Result"/>/<see cref="Result{T}"/>,
/// the exception propagates as before. A commit failure never reaches an EF Core <c>SaveChangesInterceptor</c>, so this
/// is the one place a mapping covers both moments.
/// </para>
/// <para>
/// Translate only what the caller can act on (a duplicate, an overlap, a missing referenced row), and never put the
/// exception text in the message: it can name tables, columns and values.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class ConstraintTranslator : IUnitOfWorkFailureTranslator {
///     public AppError? Translate(Exception exception) =&gt;
///         (exception as PostgresException ?? exception.InnerException as PostgresException) switch {
///             { SqlState: PostgresErrorCodes.ExclusionViolation, ConstraintName: "ex_periods_overlap" }
///                 =&gt; AppError.Conflict("The period overlaps another one.", "schedule.overlap"),
///             { SqlState: PostgresErrorCodes.UniqueViolation } =&gt; AppError.Conflict("The value is already taken."),
///             _ =&gt; null
///         };
/// }
///
/// services.AddSingleton&lt;IUnitOfWorkFailureTranslator, ConstraintTranslator&gt;();
/// </code>
/// </example>
public interface IUnitOfWorkFailureTranslator {
    /// <summary>The error for <paramref name="exception"/>, or <see langword="null"/> to let it propagate.</summary>
    /// <param name="exception">The exception the handler or the commit threw (often wrapping the provider's).</param>
    AppError? Translate(Exception exception);
}
