using System.Linq.Expressions;
using AwesomeAssertions;
using Elarion.Expressions;
using Xunit;

namespace Elarion.Tests.Expressions;

public sealed class ExprTests {
    private static readonly int[] Numbers = [1, 2, 3, 4, 5, 6];

    [Fact]
    public void Captured_IsAMemberAccessOnAClosure_NotAConstant() {
        var captured = Expr.Captured(new DateOnly(2031, 7, 15));

        captured.Should().BeAssignableTo<MemberExpression>()
            .Which.Expression.Should().BeOfType<ConstantExpression>();
        captured.Type.Should().Be<DateOnly>();
        Expression.Lambda<Func<DateOnly>>(captured).Compile()().Should().Be(new DateOnly(2031, 7, 15));
    }

    [Fact]
    public void Captured_EachCallCapturesItsOwnValue() {
        var first = Expr.Captured(1);
        var second = Expr.Captured(2);

        Expression.Lambda<Func<int>>(first).Compile()().Should().Be(1);
        Expression.Lambda<Func<int>>(second).Compile()().Should().Be(2);
    }

    [Fact]
    public void Inline_ReplacesTheParametersWithTheArguments() {
        Expression<Func<int, int, bool>> between = (value, limit) => value > 1 && value < limit;
        var parameter = Expression.Parameter(typeof(int), "n");

        var body = Expr.Inline(between, parameter, Expr.Captured(5));
        var predicate = Expression.Lambda<Func<int, bool>>(body, parameter).Compile();

        Numbers.Where(predicate).Should().Equal(2, 3, 4);
    }

    [Fact]
    public void Inline_ArgumentCountMismatch_Throws() {
        Expression<Func<int, int>> identity = value => value;

        var inline = () => Expr.Inline(identity);

        inline.Should().Throw<ArgumentException>().WithMessage("*1 parameter(s), but 0 argument(s)*");
    }

    [Fact]
    public void Inline_ArgumentOfAnotherType_Throws() {
        Expression<Func<long, long>> identity = value => value;

        var inline = () => Expr.Inline(identity, Expression.Constant(1));

        inline.Should().Throw<ArgumentException>().WithMessage("*Expression.Convert*");
    }

    [Fact]
    public void AnyOf_IsTrueWhenAnyPredicateIs() {
        var predicate = Expr.AnyOf<int>(n => n == 2, n => n == 5, n => n > 100);

        Numbers.Where(predicate.Compile()).Should().Equal(2, 5);
    }

    [Fact]
    public void AnyOf_FromASequence_IsTrueWhenAnyPredicateIs() {
        var windows = new[] { (From: 1, To: 2), (From: 5, To: 6) };

        var predicate = Expr.AnyOf(windows.Select(window =>
            (Expression<Func<int, bool>>)(n => n >= window.From && n <= window.To)));

        Numbers.Where(predicate.Compile()).Should().Equal(1, 2, 5, 6);
    }

    [Fact]
    public void AnyOf_RebindsEveryPredicateToOneParameter() {
        var predicate = Expr.AnyOf<int>(a => a == 1, b => b == 2, c => c == 3);

        var parameters = new ParameterCollector();
        parameters.Visit(predicate.Body);

        parameters.Found.Should().ContainSingle().Which.Should().BeSameAs(predicate.Parameters[0]);
    }

    [Fact]
    public void AnyOf_NoPredicates_MatchesNothing() {
        var predicate = Expr.AnyOf<int>();

        Numbers.Where(predicate.Compile()).Should().BeEmpty();
        Expr.AnyOf(Enumerable.Empty<Expression<Func<int, bool>>>()).Compile()(1).Should().BeFalse();
    }

    [Fact]
    public void AllOf_IsTrueWhenAllPredicatesAre() {
        var predicate = Expr.AllOf<int>(n => n > 1, n => n < 5, n => n % 2 == 0);

        Numbers.Where(predicate.Compile()).Should().Equal(2, 4);
    }

    [Fact]
    public void AllOf_FromASequence_IsTrueWhenAllPredicatesAre() {
        var limits = new List<Expression<Func<int, bool>>> { n => n > 2, n => n < 6 };

        Numbers.Where(Expr.AllOf(limits).Compile()).Should().Equal(3, 4, 5);
    }

    [Fact]
    public void AllOf_NoPredicates_MatchesEverything() {
        var predicate = Expr.AllOf<int>();

        Numbers.Where(predicate.Compile()).Should().Equal(Numbers);
    }

    [Fact]
    public void AnyOf_NullPredicate_Throws() {
        var anyOf = () => Expr.AnyOf<int>(n => n == 1, null!);

        anyOf.Should().Throw<ArgumentException>().WithParameterName("predicates");
    }

    [Fact]
    public void Not_NegatesThePredicate() {
        var predicate = Expr.Not<int>(n => n > 2);

        Numbers.Where(predicate.Compile()).Should().Equal(1, 2);
    }

    private sealed class ParameterCollector : ExpressionVisitor {
        public HashSet<ParameterExpression> Found { get; } = [];

        protected override Expression VisitParameter(ParameterExpression node) {
            Found.Add(node);
            return node;
        }
    }
}
