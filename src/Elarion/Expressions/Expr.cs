using System.Linq.Expressions;

namespace Elarion.Expressions;

/// <summary>
/// Building blocks for expressions assembled at run time: a value a query provider parameterizes, a lambda inlined
/// with given arguments, and predicates joined with OR or AND or negated.
/// </summary>
public static class Expr {
    /// <summary>
    /// Returns <paramref name="value"/> as a member access on a closure object, the shape the C# compiler emits for a
    /// captured local. A query provider sends it as a parameter, so one query shape has one cached plan whatever the
    /// value; <see cref="Expression.Constant(object)"/> would bake the value into the SQL text instead.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <example>
    /// <code>
    /// var invoice = Expression.Parameter(typeof(Invoice), "invoice");
    /// var body = Expr.Inline(IsOverdue.Expression, invoice, Expr.Captured(today)); // today becomes @today, not '2026-11-22'
    /// var overdue = Expression.Lambda&lt;Func&lt;Invoice, bool&gt;&gt;(body, invoice);
    /// </code>
    /// </example>
    public static Expression Captured<T>(T value) {
        Expression<Func<T>> capture = () => value;
        return capture.Body;
    }

    /// <summary>
    /// Returns the body of <paramref name="lambda"/> with its parameters replaced by <paramref name="arguments"/>, for
    /// trees built at run time. Inside a lambda written in C#, invoke the rule or expression instead and call
    /// <c>Expand()</c>.
    /// </summary>
    /// <remarks>The body is not expanded: rule invocations in it stay markers until <c>Expand()</c>.</remarks>
    /// <param name="lambda">The lambda whose body to inline.</param>
    /// <param name="arguments">One expression per parameter, each of the parameter's type or a type assignable to it
    /// by reference.</param>
    /// <exception cref="ArgumentException">The argument count or an argument type does not match.</exception>
    /// <example>
    /// <code>
    /// // A filter on a property chosen at run time, applied through the same rule.
    /// var row = Expression.Parameter(typeof(Payment), "payment");
    /// var body = Expr.Inline(IsWithinPeriod.Expression, Expression.Property(row, "PaidOn"), Expr.Captured(period));
    /// </code>
    /// </example>
    public static Expression Inline(LambdaExpression lambda, params Expression[] arguments) {
        ArgumentNullException.ThrowIfNull(lambda);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length != lambda.Parameters.Count) {
            throw new ArgumentException(
                $"The lambda takes {lambda.Parameters.Count} parameter(s), but {arguments.Length} argument(s) were given.",
                nameof(arguments));
        }

        for (var i = 0; i < arguments.Length; i++) {
            var parameter = lambda.Parameters[i];
            var argument = arguments[i]
                           ?? throw new ArgumentException($"Argument {i} is null.", nameof(arguments));
            if (!parameter.Type.IsAssignableFrom(argument.Type)) {
                throw new ArgumentException(
                    $"Argument {i} is of type {argument.Type}, which is not assignable to parameter '{parameter.Name}' "
                    + $"of type {parameter.Type}; convert it with Expression.Convert first.",
                    nameof(arguments));
            }
        }

        return ParameterReplacer.Replace(lambda.Body, lambda.Parameters, arguments);
    }

    /// <summary>
    /// Joins predicates over one row with OR: the result is true when any of them is. No predicates give a predicate
    /// that is always false, the identity of OR, so an empty set of alternatives matches no row.
    /// </summary>
    /// <param name="predicates">The alternatives. Each is rebound to one shared parameter.</param>
    /// <example>
    /// <code>
    /// // One window per bank account, each with its own dates (captured locals, so parameters in SQL).
    /// var inWindow = Expr.AnyOf(accounts.Select(account =&gt;
    ///     (Expression&lt;Func&lt;Booking, bool&gt;&gt;)(booking =&gt; booking.AccountId == account.Id
    ///         &amp;&amp; booking.BookedOn &gt;= account.From &amp;&amp; booking.BookedOn &lt;= account.To)));
    /// var bookings = await db.Bookings.Where(inWindow).ToListAsync(ct);
    /// </code>
    /// </example>
    public static Expression<Func<T, bool>> AnyOf<T>(params Expression<Func<T, bool>>[] predicates) {
        return Join(predicates, any: true);
    }

    /// <inheritdoc cref="AnyOf{T}(Expression{Func{T, bool}}[])"/>
    public static Expression<Func<T, bool>> AnyOf<T>(IEnumerable<Expression<Func<T, bool>>> predicates) {
        return Join(predicates, any: true);
    }

    /// <summary>
    /// Joins predicates over one row with AND: the result is true when all of them are. No predicates give a predicate
    /// that is always true, the identity of AND, so an empty set of conditions matches every row.
    /// </summary>
    /// <param name="predicates">The conditions. Each is rebound to one shared parameter.</param>
    /// <example>
    /// <code>
    /// var filter = Expr.AllOf(request.Filters.Select(ToPredicate));
    /// var invoices = await db.Invoices.Where(filter).ToListAsync(ct);
    /// </code>
    /// </example>
    public static Expression<Func<T, bool>> AllOf<T>(params Expression<Func<T, bool>>[] predicates) {
        return Join(predicates, any: false);
    }

    /// <inheritdoc cref="AllOf{T}(Expression{Func{T, bool}}[])"/>
    public static Expression<Func<T, bool>> AllOf<T>(IEnumerable<Expression<Func<T, bool>>> predicates) {
        return Join(predicates, any: false);
    }

    /// <summary>Negates a predicate.</summary>
    /// <param name="predicate">The predicate to negate.</param>
    /// <example>
    /// <code>
    /// var unsettled = await db.Invoices.Where(Expr.Not(IsSettled.Expression)).Expand().ToListAsync(ct);
    /// </code>
    /// </example>
    public static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> predicate) {
        ArgumentNullException.ThrowIfNull(predicate);
        return Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);
    }

    private static Expression<Func<T, bool>> Join<T>(IEnumerable<Expression<Func<T, bool>>> predicates, bool any) {
        ArgumentNullException.ThrowIfNull(predicates);
        ParameterExpression? parameter = null;
        Expression? body = null;
        foreach (var predicate in predicates) {
            if (predicate is null) throw new ArgumentException("A predicate is null.", nameof(predicates));

            if (parameter is null) {
                parameter = predicate.Parameters[0];
                body = predicate.Body;
                continue;
            }

            var rebound = ParameterReplacer.Replace(predicate.Body, predicate.Parameters, [parameter]);
            body = any ? Expression.OrElse(body!, rebound) : Expression.AndAlso(body!, rebound);
        }

        return parameter is null
            ? Expression.Lambda<Func<T, bool>>(Expression.Constant(!any), Expression.Parameter(typeof(T), "x"))
            : Expression.Lambda<Func<T, bool>>(body!, parameter);
    }
}
