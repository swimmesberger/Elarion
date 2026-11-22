using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Elarion.Expressions;

/// <summary>
/// Expands rule invocations in expression trees and queries, and lets a plain expression be invoked inside another
/// one the way a <see cref="Rule{TInput, TResult}"/> is.
/// </summary>
public static class RuleExpressionExtensions {
    private static readonly ConditionalWeakTable<LambdaExpression, Delegate> CompiledExpressions = new();

    /// <summary>
    /// Returns <paramref name="expression"/> with every rule invocation inlined: each <c>rule.Invoke(…)</c> and each
    /// <c>expression.Invoke(…)</c> becomes the invoked body with its parameters substituted by the call's arguments,
    /// recursively. A tree without invocations comes back as the same instance.
    /// </summary>
    /// <param name="expression">The expression to expand.</param>
    /// <exception cref="NotSupportedException">An invoked rule is reached through an expression that cannot be
    /// evaluated without compiling code, such as a method call or a lambda parameter.</exception>
    /// <exception cref="InvalidOperationException">An invoked rule is null, or invokes itself.</exception>
    /// <example>
    /// <code>
    /// Expression&lt;Func&lt;Invoice, bool&gt;&gt; overdue = invoice =&gt; invoice.DueOn &lt; today &amp;&amp; OpenAmount.Invoke(invoice) &gt; 0;
    /// Expression&lt;Func&lt;Invoice, bool&gt;&gt; expanded = overdue.Expand();
    /// // invoice =&gt; invoice.DueOn &lt; today &amp;&amp; invoice.Gross - invoice.Payments.Sum(p =&gt; p.Amount) &gt; 0
    /// </code>
    /// </example>
    public static Expression Expand(this Expression expression) {
        ArgumentNullException.ThrowIfNull(expression);
        return new RuleExpander().Visit(expression);
    }

    /// <inheritdoc cref="Expand(Expression)"/>
    public static Expression<TDelegate> Expand<TDelegate>(this Expression<TDelegate> expression) {
        ArgumentNullException.ThrowIfNull(expression);
        return (Expression<TDelegate>)new RuleExpander().Visit(expression);
    }

    /// <summary>
    /// Returns the query with every rule invocation in the expressions it is built from inlined, so a query provider
    /// such as EF Core translates the rules' bodies. Call it after the last operator that invokes a rule: operators
    /// appended to the returned query are not expanded.
    /// </summary>
    /// <remarks>
    /// A query that reaches EF Core without <c>Expand()</c> fails with "could not be translated", naming
    /// <c>Invoke</c>, when the invocation is in a filter, an ordering or a join. In the final projection EF Core
    /// evaluates what it cannot translate on the client instead: it refuses a rule held in a static field (a constant
    /// captured by the client projection), but runs a rule held in a captured local in memory, on the materialized
    /// entity, without the navigations SQL would have joined.
    /// </remarks>
    /// <param name="query">The query to expand.</param>
    /// <exception cref="NotSupportedException">An invoked rule is reached through an expression that cannot be
    /// evaluated without compiling code.</exception>
    /// <exception cref="InvalidOperationException">An invoked rule is null, or invokes itself.</exception>
    /// <example>
    /// <code>
    /// var rows = await db.Invoices
    ///     .Where(invoice =&gt; !IsSettled.Invoke(invoice))
    ///     .Select(invoice =&gt; new OpenInvoiceRow(invoice.Id, OpenAmount.Invoke(invoice)))
    ///     .Expand()                      // after the last operator that invokes a rule
    ///     .ToListAsync(ct);
    /// </code>
    /// </example>
    public static IQueryable<T> Expand<T>(this IQueryable<T> query) {
        ArgumentNullException.ThrowIfNull(query);
        var expanded = new RuleExpander().Visit(query.Expression);
        return ReferenceEquals(expanded, query.Expression) ? query : query.Provider.CreateQuery<T>(expanded);
    }

    /// <summary>
    /// Runs <paramref name="expression"/> on <paramref name="input"/> in memory, compiled once per expression
    /// instance. Inside another expression tree the call is a marker that <c>Expand()</c> replaces with the
    /// expression's body, as it does for <see cref="Rule{TInput, TResult}.Invoke"/>.
    /// </summary>
    /// <param name="expression">The expression to invoke, usually a static field or a captured local.</param>
    /// <param name="input">The argument.</param>
    /// <example>
    /// <code>
    /// Expression&lt;Func&lt;Invoice, decimal&gt;&gt; openAmount = invoice =&gt; invoice.Gross - invoice.Paid;
    /// var open = await db.Invoices.Where(invoice =&gt; openAmount.Invoke(invoice) &gt; 0).Expand().ToListAsync(ct);
    /// </code>
    /// </example>
    public static TResult Invoke<TInput, TResult>(this Expression<Func<TInput, TResult>> expression, TInput input) {
        ArgumentNullException.ThrowIfNull(expression);
        return ((Func<TInput, TResult>)CompiledExpressions.GetValue(expression, Rule.Compile))(input);
    }

    /// <inheritdoc cref="Invoke{TInput, TResult}(Expression{Func{TInput, TResult}}, TInput)"/>
    /// <param name="expression">The expression to invoke, usually a static field or a captured local.</param>
    /// <param name="input1">The first argument.</param>
    /// <param name="input2">The second argument.</param>
    public static TResult Invoke<TInput1, TInput2, TResult>(
        this Expression<Func<TInput1, TInput2, TResult>> expression,
        TInput1 input1,
        TInput2 input2) {
        ArgumentNullException.ThrowIfNull(expression);
        return ((Func<TInput1, TInput2, TResult>)CompiledExpressions.GetValue(expression, Rule.Compile))(
            input1, input2);
    }
}
