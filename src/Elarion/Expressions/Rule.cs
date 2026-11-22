using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Elarion.Expressions;

/// <summary>
/// Creates a rule over one to four inputs (<see cref="Rule{TInput, TResult}"/> to
/// <see cref="Rule{TInput1, TInput2, TInput3, TInput4, TResult}"/>) with its types inferred from the lambda.
/// </summary>
/// <example>
/// <code>
/// public static class InvoiceRules {
///     public static readonly Rule&lt;Invoice, decimal&gt; OpenAmount =
///         Rule.Create((Invoice invoice) =&gt; invoice.Gross - invoice.Payments.Sum(p =&gt; p.Amount));
///
///     public static readonly Rule&lt;Invoice, bool&gt; IsSettled =
///         Rule.Create((Invoice invoice) =&gt; OpenAmount.Invoke(invoice) &lt;= 0);
/// }
/// </code>
/// </example>
public static class Rule {
    /// <summary>Creates a rule over one input from <paramref name="expression"/>.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public static Rule<TInput, TResult> Create<TInput, TResult>(Expression<Func<TInput, TResult>> expression) {
        return new Rule<TInput, TResult>(expression);
    }

    /// <summary>Creates a rule over two inputs from <paramref name="expression"/>.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public static Rule<TInput1, TInput2, TResult> Create<TInput1, TInput2, TResult>(
        Expression<Func<TInput1, TInput2, TResult>> expression) {
        return new Rule<TInput1, TInput2, TResult>(expression);
    }

    /// <summary>Creates a rule over three inputs from <paramref name="expression"/>.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public static Rule<TInput1, TInput2, TInput3, TResult> Create<TInput1, TInput2, TInput3, TResult>(
        Expression<Func<TInput1, TInput2, TInput3, TResult>> expression) {
        return new Rule<TInput1, TInput2, TInput3, TResult>(expression);
    }

    /// <summary>Creates a rule over four inputs from <paramref name="expression"/>.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public static Rule<TInput1, TInput2, TInput3, TInput4, TResult> Create<TInput1, TInput2, TInput3, TInput4, TResult>(
        Expression<Func<TInput1, TInput2, TInput3, TInput4, TResult>> expression) {
        return new Rule<TInput1, TInput2, TInput3, TInput4, TResult>(expression);
    }

    /// <summary>
    /// Compiles a rule's expression for in-memory use. Without dynamic code (NativeAOT) the expression interpreter
    /// runs it; System.Linq.Expressions would choose the interpreter there by itself, and saying so keeps the choice
    /// visible.
    /// </summary>
    internal static Delegate Compile(LambdaExpression expression) {
        return expression.Compile(preferInterpretation: !RuntimeFeature.IsDynamicCodeSupported);
    }

    /// <summary>
    /// Returns the delegate cached in <paramref name="compiled"/>, compiling <paramref name="expression"/> into it on
    /// first use. A race compiles twice and keeps the first result; both delegates are equivalent, and no lock is held
    /// while compiling.
    /// </summary>
    internal static TDelegate GetOrCompile<TDelegate>(ref TDelegate? compiled, LambdaExpression expression)
        where TDelegate : Delegate {
        var current = Volatile.Read(ref compiled);
        if (current is not null) return current;

        return Interlocked.CompareExchange(ref compiled, (TDelegate)Compile(expression), null) ?? compiled;
    }
}

/// <summary>
/// A domain rule over one input, defined once as an expression: <see cref="Expression"/> goes into a query and is
/// translated by the provider, <see cref="Invoke"/> runs the same definition in memory on a loaded object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composition is written as a call.</b> Inside another expression, <c>rule.Invoke(x)</c> is a marker that
/// <see cref="RuleExpressionExtensions.Expand{T}(IQueryable{T})"/> (or <c>Expand()</c> on an expression) replaces with
/// the rule's body, its parameter substituted by the call's argument. Rules invoke rules; the expansion is
/// recursive. Without <c>Expand()</c> a query provider sees a method it cannot translate.
/// </para>
/// <para>
/// <b>Where the rule lives.</b> <c>Expand()</c> reads the rule instance out of the expression tree without compiling
/// code, so the rule must be reachable through a static field or property, a captured local or a field of a
/// captured object. A rule obtained from a method call or from a lambda parameter cannot be expanded.
/// </para>
/// <para>
/// <b>In memory.</b> <see cref="Invoke"/> runs a delegate compiled once, on first use, and cached; it is
/// thread-safe. A rule over a navigation (<c>invoice.Payments.Sum(…)</c>) needs that navigation loaded, where SQL
/// would have joined it.
/// </para>
/// </remarks>
/// <typeparam name="TInput">The object the rule is about, usually an entity.</typeparam>
/// <typeparam name="TResult">What the rule derives: a value, or <see langword="bool"/> for a condition.</typeparam>
/// <example>
/// <code>
/// // One definition…
/// static readonly Rule&lt;Invoice, decimal&gt; OpenAmount =
///     Rule.Create((Invoice invoice) =&gt; invoice.Gross - invoice.Payments.Sum(p =&gt; p.Amount));
///
/// // …in SQL: composed into a filter and a projection, expanded once before EF sees the query.
/// var rows = await db.Invoices
///     .Where(invoice =&gt; OpenAmount.Invoke(invoice) &gt; 0)
///     .Select(invoice =&gt; new OpenInvoiceRow(invoice.Id, OpenAmount.Invoke(invoice)))
///     .Expand()
///     .ToListAsync(ct);
///
/// // …and in memory, on a loaded invoice.
/// decimal open = OpenAmount.Invoke(invoice);
/// </code>
/// </example>
public sealed class Rule<TInput, TResult> : IRuleExpression {
    private Func<TInput, TResult>? _compiled;

    /// <summary>Creates a rule from its definition.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public Rule(Expression<Func<TInput, TResult>> expression) {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
    }

    /// <summary>
    /// The rule's definition. Pass it to a query operator directly (<c>query.Where(IsSettled.Expression)</c>); when
    /// it invokes other rules, call <c>Expand()</c> on the query as well.
    /// </summary>
    public Expression<Func<TInput, TResult>> Expression { get; }

    /// <summary>
    /// The rule compiled to a delegate, on first access, and cached. Without dynamic code (NativeAOT) the delegate
    /// runs on the expression interpreter.
    /// </summary>
    public Func<TInput, TResult> Compiled => Rule.GetOrCompile(ref _compiled, Expression);

    LambdaExpression IRuleExpression.Lambda => Expression;

    /// <summary>
    /// Runs the rule in memory on <paramref name="input"/>. Inside an expression tree the call is the marker that
    /// <c>Expand()</c> replaces with the rule's body.
    /// </summary>
    /// <param name="input">The object to apply the rule to.</param>
    public TResult Invoke(TInput input) {
        return Compiled(input);
    }

    /// <summary>Returns the rule's expression as text.</summary>
    public override string ToString() {
        return Expression.ToString();
    }
}

/// <summary>
/// A domain rule over two inputs, defined once as an expression; see <see cref="Rule{TInput, TResult}"/>. The second
/// input usually carries a value the rule cannot reach from the first: a date, a threshold, or an aggregate joined
/// once per row.
/// </summary>
/// <typeparam name="TInput1">The first input, usually the entity the rule is about.</typeparam>
/// <typeparam name="TInput2">The second input.</typeparam>
/// <typeparam name="TResult">What the rule derives: a value, or <see langword="bool"/> for a condition.</typeparam>
/// <example>
/// <code>
/// static readonly Rule&lt;Invoice, DateOnly, bool&gt; IsOverdue =
///     Rule.Create((Invoice invoice, DateOnly today) =&gt; invoice.DueOn &lt; today &amp;&amp; OpenAmount.Invoke(invoice) &gt; 0);
///
/// var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
/// var overdue = await db.Invoices.Where(invoice =&gt; IsOverdue.Invoke(invoice, today)).Expand().ToListAsync(ct);
/// </code>
/// </example>
public sealed class Rule<TInput1, TInput2, TResult> : IRuleExpression {
    private Func<TInput1, TInput2, TResult>? _compiled;

    /// <summary>Creates a rule from its definition.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public Rule(Expression<Func<TInput1, TInput2, TResult>> expression) {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
    }

    /// <summary>The rule's definition.</summary>
    public Expression<Func<TInput1, TInput2, TResult>> Expression { get; }

    /// <summary>
    /// The rule compiled to a delegate, on first access, and cached. Without dynamic code (NativeAOT) the delegate
    /// runs on the expression interpreter.
    /// </summary>
    public Func<TInput1, TInput2, TResult> Compiled => Rule.GetOrCompile(ref _compiled, Expression);

    LambdaExpression IRuleExpression.Lambda => Expression;

    /// <summary>
    /// Runs the rule in memory. Inside an expression tree the call is the marker that <c>Expand()</c> replaces with
    /// the rule's body.
    /// </summary>
    /// <param name="input1">The first input.</param>
    /// <param name="input2">The second input.</param>
    public TResult Invoke(TInput1 input1, TInput2 input2) {
        return Compiled(input1, input2);
    }

    /// <summary>Returns the rule's expression as text.</summary>
    public override string ToString() {
        return Expression.ToString();
    }
}

/// <summary>
/// A domain rule over three inputs, defined once as an expression; see <see cref="Rule{TInput, TResult}"/>. The
/// further inputs carry values the rule cannot reach from the first: several aggregates joined once per row, or an
/// aggregate and a date.
/// </summary>
/// <typeparam name="TInput1">The first input, usually the entity the rule is about.</typeparam>
/// <typeparam name="TInput2">The second input.</typeparam>
/// <typeparam name="TInput3">The third input.</typeparam>
/// <typeparam name="TResult">What the rule derives: a value, or <see langword="bool"/> for a condition.</typeparam>
/// <example>
/// <code>
/// // The paid and the credited sum are joined once per row and passed in, instead of summed at every use.
/// static readonly Rule&lt;Invoice, decimal, decimal, decimal&gt; OpenGiven =
///     Rule.Create((Invoice invoice, decimal paid, decimal credited) =&gt; invoice.Gross - paid - credited);
///
/// decimal open = OpenGiven.Invoke(invoice, Paid.Invoke(invoice), Credited.Invoke(invoice));
/// </code>
/// </example>
public sealed class Rule<TInput1, TInput2, TInput3, TResult> : IRuleExpression {
    private Func<TInput1, TInput2, TInput3, TResult>? _compiled;

    /// <summary>Creates a rule from its definition.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public Rule(Expression<Func<TInput1, TInput2, TInput3, TResult>> expression) {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
    }

    /// <summary>The rule's definition.</summary>
    public Expression<Func<TInput1, TInput2, TInput3, TResult>> Expression { get; }

    /// <summary>
    /// The rule compiled to a delegate, on first access, and cached. Without dynamic code (NativeAOT) the delegate
    /// runs on the expression interpreter.
    /// </summary>
    public Func<TInput1, TInput2, TInput3, TResult> Compiled => Rule.GetOrCompile(ref _compiled, Expression);

    LambdaExpression IRuleExpression.Lambda => Expression;

    /// <summary>
    /// Runs the rule in memory. Inside an expression tree the call is the marker that <c>Expand()</c> replaces with
    /// the rule's body.
    /// </summary>
    /// <param name="input1">The first input.</param>
    /// <param name="input2">The second input.</param>
    /// <param name="input3">The third input.</param>
    public TResult Invoke(TInput1 input1, TInput2 input2, TInput3 input3) {
        return Compiled(input1, input2, input3);
    }

    /// <summary>Returns the rule's expression as text.</summary>
    public override string ToString() {
        return Expression.ToString();
    }
}

/// <summary>
/// A domain rule over four inputs, defined once as an expression; see <see cref="Rule{TInput, TResult}"/> and
/// <see cref="Rule{TInput1, TInput2, TInput3, TResult}"/>.
/// </summary>
/// <typeparam name="TInput1">The first input, usually the entity the rule is about.</typeparam>
/// <typeparam name="TInput2">The second input.</typeparam>
/// <typeparam name="TInput3">The third input.</typeparam>
/// <typeparam name="TInput4">The fourth input.</typeparam>
/// <typeparam name="TResult">What the rule derives: a value, or <see langword="bool"/> for a condition.</typeparam>
/// <example>
/// <code>
/// static readonly Rule&lt;Invoice, decimal, decimal, DateOnly, bool&gt; IsDunnable =
///     Rule.Create((Invoice invoice, decimal paid, decimal credited, DateOnly today) =&gt;
///         invoice.DueOn &lt; today &amp;&amp; OpenGiven.Invoke(invoice, paid, credited) &gt; 0);
/// </code>
/// </example>
public sealed class Rule<TInput1, TInput2, TInput3, TInput4, TResult> : IRuleExpression {
    private Func<TInput1, TInput2, TInput3, TInput4, TResult>? _compiled;

    /// <summary>Creates a rule from its definition.</summary>
    /// <param name="expression">The rule's definition, the one expression used in queries and in memory.</param>
    public Rule(Expression<Func<TInput1, TInput2, TInput3, TInput4, TResult>> expression) {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
    }

    /// <summary>The rule's definition.</summary>
    public Expression<Func<TInput1, TInput2, TInput3, TInput4, TResult>> Expression { get; }

    /// <summary>
    /// The rule compiled to a delegate, on first access, and cached. Without dynamic code (NativeAOT) the delegate
    /// runs on the expression interpreter.
    /// </summary>
    public Func<TInput1, TInput2, TInput3, TInput4, TResult> Compiled => Rule.GetOrCompile(ref _compiled, Expression);

    LambdaExpression IRuleExpression.Lambda => Expression;

    /// <summary>
    /// Runs the rule in memory. Inside an expression tree the call is the marker that <c>Expand()</c> replaces with
    /// the rule's body.
    /// </summary>
    /// <param name="input1">The first input.</param>
    /// <param name="input2">The second input.</param>
    /// <param name="input3">The third input.</param>
    /// <param name="input4">The fourth input.</param>
    public TResult Invoke(TInput1 input1, TInput2 input2, TInput3 input3, TInput4 input4) {
        return Compiled(input1, input2, input3, input4);
    }

    /// <summary>Returns the rule's expression as text.</summary>
    public override string ToString() {
        return Expression.ToString();
    }
}

/// <summary>What the expander reads from a rule instance it found in an expression tree.</summary>
internal interface IRuleExpression {
    LambdaExpression Lambda { get; }
}
