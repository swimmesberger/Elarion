using System.Collections.Concurrent;
using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Expressions;
using Xunit;

namespace Elarion.Tests.Expressions;

public sealed class RuleTests {
    public sealed class Payment {
        public decimal Amount { get; init; }
    }

    public sealed class Invoice {
        public int Id { get; init; }
        public decimal Gross { get; init; }
        public DateOnly DueOn { get; init; }
        public List<Payment> Payments { get; init; } = [];
    }

    public sealed record InvoiceRow(int Id, decimal Open);

    public sealed class Node {
        public Node? Parent { get; init; }
    }

    private static readonly Rule<Invoice, decimal> Paid =
        Rule.Create((Invoice invoice) => invoice.Payments.Sum(p => p.Amount));

    private static readonly Rule<Invoice, decimal> OpenAmount =
        Rule.Create((Invoice invoice) => invoice.Gross - Paid.Invoke(invoice));

    private static readonly Rule<Invoice, bool> IsSettled =
        Rule.Create((Invoice invoice) => OpenAmount.Invoke(invoice) <= 0);

    private static readonly Rule<Invoice, DateOnly, bool> IsOverdue =
        Rule.Create((Invoice invoice, DateOnly today) => invoice.DueOn < today && !IsSettled.Invoke(invoice));

    private static readonly Expression<Func<Invoice, decimal>> GrossTimesTwo = invoice => invoice.Gross * 2;

    // Invokes itself through its static field: fine in memory, no finite expansion.
    private static readonly Rule<Node, int> Depth =
        Rule.Create((Node node) => node.Parent == null ? 0 : Depth!.Invoke(node.Parent) + 1);

    private static readonly DateOnly Today = new(2026, 11, 22);

    private static readonly Invoice[] Invoices = [
        new() { Id = 1, Gross = 100m, DueOn = new(2026, 11, 1), Payments = [new() { Amount = 100m }] },
        new() { Id = 2, Gross = 100m, DueOn = new(2026, 11, 1), Payments = [new() { Amount = 40m }] },
        new() { Id = 3, Gross = 50m, DueOn = new(2026, 12, 1) },
        new() { Id = 4, Gross = 80m, DueOn = new(2026, 10, 1), Payments = [new() { Amount = 50m }, new() { Amount = 30m }] },
    ];

    [Fact]
    public void Invoke_RunsTheExpressionInMemory() {
        Invoices.Select(OpenAmount.Invoke).Should().Equal(0m, 60m, 50m, 0m);
        Invoices.Select(IsSettled.Invoke).Should().Equal(true, false, false, true);
        Invoices.Select(invoice => IsOverdue.Invoke(invoice, Today)).Should().Equal(false, true, false, false);
    }

    [Fact]
    public void Compiled_IsCompiledOnceAndCached() {
        var rule = Rule.Create((Invoice invoice) => invoice.Gross);

        rule.Compiled.Should().BeSameAs(rule.Compiled);
    }

    [Fact]
    public void Compiled_ConcurrentFirstUse_GivesEveryCallerTheSameDelegate() {
        var rule = Rule.Create((Invoice invoice) => invoice.Gross + 1);
        var delegates = new ConcurrentBag<Func<Invoice, decimal>>();

        Parallel.For(0, 64, _ => delegates.Add(rule.Compiled));

        delegates.Distinct().Should().ContainSingle().Which.Should().BeSameAs(rule.Compiled);
    }

    [Fact]
    public void Expand_InlinesTheRuleBody() {
        Expression<Func<Invoice, bool>> open = invoice => OpenAmount.Invoke(invoice) > 0;

        var expanded = open.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        expanded.Parameters.Should().Equal(open.Parameters);
        expanded.ToString().Should().Contain("invoice.Gross").And.Contain("invoice.Payments.Sum(p => p.Amount)");
        Invoices.Select(expanded.Compile()).Should().Equal(false, true, true, false);
    }

    [Fact]
    public void Expand_RulesThatInvokeRules_InlinesRecursively() {
        var expanded = IsSettled.Expression.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        Invoices.Select(expanded.Compile()).Should().Equal(Invoices.Select(IsSettled.Invoke));
    }

    [Fact]
    public void Expand_TwoInputRule_SubstitutesBothArguments() {
        var cutoff = Today;
        Expression<Func<Invoice, bool>> overdue = invoice => IsOverdue.Invoke(invoice, cutoff);

        var expanded = overdue.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        expanded.ToString().Should().Contain("cutoff");
        Invoices.Select(expanded.Compile()).Should().Equal(false, true, false, false);
    }

    [Fact]
    public void Expand_RuleInAProjection_InlinesIntoTheConstructorArguments() {
        Expression<Func<Invoice, InvoiceRow>> projection = invoice => new InvoiceRow(invoice.Id, OpenAmount.Invoke(invoice));

        var expanded = projection.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        Invoices.Select(expanded.Compile()).Should().Equal(
            new InvoiceRow(1, 0m), new InvoiceRow(2, 60m), new InvoiceRow(3, 50m), new InvoiceRow(4, 0m));
    }

    [Fact]
    public void Expand_RuleInACapturedLocal_Inlines() {
        var local = Rule.Create((Invoice invoice) => invoice.Gross > 60m);
        Expression<Func<Invoice, bool>> large = invoice => local.Invoke(invoice);

        var expanded = large.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        Invoices.Select(expanded.Compile()).Should().Equal(true, true, false, true);
    }

    [Fact]
    public void Expand_InvokeOnAnExpression_InlinesLikeARule() {
        Expression<Func<Invoice, decimal>> paidTwice = invoice => invoice.Payments.Sum(p => p.Amount) * 2;
        Expression<Func<Invoice, decimal, decimal>> plus = (invoice, amount) => invoice.Gross + amount;
        Expression<Func<Invoice, decimal>> composed =
            invoice => GrossTimesTwo.Invoke(invoice) - paidTwice.Invoke(invoice) + plus.Invoke(invoice, 1m);

        var expanded = composed.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        Invoices.Select(expanded.Compile()).Should().Equal(Invoices.Select(composed.Compile()));
        Invoices.Select(expanded.Compile()).Should().Equal(101m, 221m, 151m, 81m);
    }

    [Fact]
    public void Expand_QuotedExpressionLiteral_Inlines() {
        Expression<Func<Invoice, decimal>> composed =
            invoice => ((Expression<Func<decimal, decimal>>)(gross => gross / 2)).Invoke(invoice.Gross);

        var expanded = composed.Expand();

        ContainsInvoke(expanded).Should().BeFalse();
        Invoices.Select(expanded.Compile()).Should().Equal(50m, 50m, 25m, 40m);
    }

    [Fact]
    public void Expand_WithoutInvocations_ReturnsTheSameInstance() {
        Expression<Func<Invoice, bool>> plain = invoice => invoice.Gross > 0;

        plain.Expand().Should().BeSameAs(plain);
    }

    [Fact]
    public void Expand_RuleFromALambdaParameter_ThrowsNotSupported() {
        Expression<Func<Rule<Invoice, bool>, Invoice, bool>> viaParameter = (rule, invoice) => rule.Invoke(invoice);

        var expand = () => viaParameter.Expand();

        expand.Should().Throw<NotSupportedException>().WithMessage("*static field or property*");
    }

    [Fact]
    public void Expand_RuleFromAMethodCall_ThrowsNotSupported() {
        Expression<Func<Invoice, bool>> viaMethod = invoice => GetRule().Invoke(invoice);

        var expand = () => viaMethod.Expand();

        expand.Should().Throw<NotSupportedException>().WithMessage("*GetRule()*");
    }

    [Fact]
    public void Expand_NullRule_ThrowsInvalidOperation() {
        Rule<Invoice, bool>? missing = null;
        Expression<Func<Invoice, bool>> viaNull = invoice => missing!.Invoke(invoice);

        var expand = () => viaNull.Expand();

        expand.Should().Throw<InvalidOperationException>().WithMessage("*null*");
    }

    [Fact]
    public void Expand_RuleThatInvokesItself_ThrowsInvalidOperation() {
        var leaf = new Node { Parent = new Node { Parent = new Node() } };
        Depth.Invoke(leaf).Should().Be(2);

        var expand = () => Depth.Expression.Expand();

        expand.Should().Throw<InvalidOperationException>().WithMessage("*invokes itself*");
    }

    [Fact]
    public void ExpandQuery_InlinesEveryOperator() {
        var query = Invoices.AsQueryable()
            .Where(invoice => !IsSettled.Invoke(invoice))
            .OrderByDescending(invoice => OpenAmount.Invoke(invoice))
            .Select(invoice => new InvoiceRow(invoice.Id, OpenAmount.Invoke(invoice)));

        var expanded = query.Expand();

        ContainsInvoke(expanded.Expression).Should().BeFalse();
        expanded.ToList().Should().Equal(new InvoiceRow(2, 60m), new InvoiceRow(3, 50m));
    }

    [Fact]
    public void ExpandQuery_OperatorsAppendedAfterExpand_AreNotExpanded() {
        var expanded = Invoices.AsQueryable().Expand().Where(invoice => IsSettled.Invoke(invoice));

        ContainsInvoke(expanded.Expression).Should().BeTrue();
    }

    [Fact]
    public void ExpandQuery_WithoutInvocations_ReturnsTheSameQuery() {
        var query = Invoices.AsQueryable().Where(invoice => invoice.Gross > 0);

        query.Expand().Should().BeSameAs(query);
    }

    [Fact]
    public void InvokeOnAnExpression_RunsInMemory() {
        GrossTimesTwo.Invoke(Invoices[2]).Should().Be(100m);
        ((Expression<Func<Invoice, DateOnly, bool>>)((invoice, today) => invoice.DueOn < today))
            .Invoke(Invoices[0], Today).Should().BeTrue();
    }

    [Fact]
    public void ToString_ShowsTheExpression() {
        OpenAmount.ToString().Should().Be(OpenAmount.Expression.ToString());
    }

    private static Rule<Invoice, bool> GetRule() {
        return IsSettled;
    }

    private static bool ContainsInvoke(Expression expression) {
        var finder = new InvokeFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class InvokeFinder : ExpressionVisitor {
        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node) {
            if (node.Method.Name == "Invoke") Found = true;
            return base.VisitMethodCall(node);
        }
    }
}
