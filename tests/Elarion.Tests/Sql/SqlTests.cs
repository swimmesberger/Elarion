using AwesomeAssertions;
using Elarion.Sql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

public sealed class SqlTests {
    [Fact]
    public void ScalarValues_BecomeSequentialParameters() {
        var status = "active";
        var id = 42;

        var sql = new SqlStatement($"UPDATE orders SET status = {status} WHERE id = {id}");

        sql.Text.Should().Be("UPDATE orders SET status = @p0 WHERE id = @p1");
        sql.ParameterValues.Should().Equal("active", 42);
    }

    [Fact]
    public void NullValue_BindsAsParameter() {
        string? note = null;

        var sql = new SqlStatement($"UPDATE orders SET note = {note}");

        sql.Text.Should().Be("UPDATE orders SET note = @p0");
        sql.ParameterValues.Should().Equal(new object?[] { null });
    }

    [Fact]
    public void Collection_ExpandsToParameterList() {
        int[] ids = [1, 2, 3];

        var sql = new SqlStatement($"SELECT * FROM orders WHERE id IN {ids}");

        sql.Text.Should().Be("SELECT * FROM orders WHERE id IN (@p0, @p1, @p2)");
        sql.ParameterValues.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void EmptyCollection_FailsLoud() {
        int[] ids = [];

        var sql = new SqlStatement($"SELECT * FROM orders WHERE id IN {ids}");

        // "(NULL)" would silently flip NOT IN to match nothing, and an untyped never-matching
        // subquery fails PostgreSQL's type inference — so the empty set fails at build time.
        var act = () => sql.Text;
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty collection*");
    }

    [Fact]
    public void StringAndByteArray_StayScalar() {
        var name = "abc";
        var payload = new byte[] { 1, 2 };

        var sql = new SqlStatement($"SELECT * FROM t WHERE name = {name} AND payload = {payload}");

        sql.Text.Should().Be("SELECT * FROM t WHERE name = @p0 AND payload = @p1");
        sql.ParameterValues.Should().HaveCount(2);
    }

    [Fact]
    public void VerbatimSplice_TrustedIdentifier() {
        // A dynamic trusted identifier (a validated table/column/sort name) splices via Verbatim;
        // there is no ':raw' format — a raw string interpolated into a query binds as a parameter.
        var table = "orders";

        var sql = new SqlStatement($"SELECT count(*) FROM {SqlStatement.Verbatim(table)}");

        sql.Text.Should().Be("SELECT count(*) FROM orders");
        sql.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void Fragment_SplicesWithRenumberedParameters() {
        var status = "open";
        var limit = 10;
        var where = new SqlStatement($"WHERE status = {status}");

        var sql = new SqlStatement($"SELECT * FROM orders {where} LIMIT {limit}");

        sql.Text.Should().Be("SELECT * FROM orders WHERE status = @p0 LIMIT @p1");
        sql.ParameterValues.Should().Equal("open", 10);
    }

    [Fact]
    public void NestedFragments_ComposeRecursively() {
        var inner = new SqlStatement($"price > {5m}");
        var outer = new SqlStatement($"WHERE {inner} AND qty < {7}");

        var sql = new SqlStatement($"SELECT * FROM t {outer}");

        sql.Text.Should().Be("SELECT * FROM t WHERE price > @p0 AND qty < @p1");
        sql.ParameterValues.Should().Equal(5m, 7);
    }

    [Fact]
    public void FragmentReuse_RebindsParametersPerStatement() {
        var shared = new SqlStatement($"status = {"open"}");

        var first = new SqlStatement($"SELECT 1 WHERE {shared}");
        var second = new SqlStatement($"SELECT 2 WHERE {shared} AND id = {3}");

        first.Text.Should().Be("SELECT 1 WHERE status = @p0");
        second.Text.Should().Be("SELECT 2 WHERE status = @p0 AND id = @p1");
        second.ParameterValues.Should().Equal("open", 3);
    }

    [Fact]
    public void SqlTypedValue_SplicesAsFragmentViaTypedOverload() {
        var orderBy = SqlStatement.Verbatim("ORDER BY created_at DESC");

        var sql = new SqlStatement($"SELECT * FROM orders {orderBy}");

        sql.Text.Should().Be("SELECT * FROM orders ORDER BY created_at DESC");
    }

    [Fact]
    public void PlainText_HasNoParameters() {
        var sql = SqlStatement.Verbatim("SELECT 1");

        sql.Text.Should().Be("SELECT 1");
        sql.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void ConstantOnlyInterpolation_StillParameterizes() {
        // A constant-only interpolated string is a C# constant expression; with a string overload in
        // play, overload resolution would prefer it and splice the hole as text. The API deliberately
        // has no string overloads, so even constants bind as parameters.
        const string status = "open";

        var sql = new SqlStatement($"status = {status}");

        sql.Text.Should().Be("status = @p0");
        sql.ParameterValues.Should().Equal("open");
    }

    [Fact]
    public void Empty_RendersEmptyWithNoParameters() {
        SqlStatement.Empty.Text.Should().Be("");
        SqlStatement.Empty.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void Empty_SplicesAsNoOp() {
        var sql = new SqlStatement($"SELECT 1 {SqlStatement.Empty}");

        sql.Text.Should().Be("SELECT 1 ");
        sql.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void OperatorPlus_ConcatenatesAndRenumbers() {
        var left = new SqlStatement($"WHERE a = {1}");
        var right = new SqlStatement($" AND b = {2}");

        var combined = left + right;

        combined.Text.Should().Be("WHERE a = @p0 AND b = @p1");
        combined.ParameterValues.Should().Equal(1, 2);
    }

    [Fact]
    public void VerbatimFragment_InlinesAsLiteral() {
        // A pure-literal fragment (Verbatim) spliced into a query carries no parameters and materializes
        // identically to inline text — the identifier-splice fast path.
        var select = SqlStatement.Verbatim("SELECT a, b FROM t");

        var sql = new SqlStatement($"{select} WHERE id = {7}");

        sql.Text.Should().Be("SELECT a, b FROM t WHERE id = @p0");
        sql.ParameterValues.Should().Equal(7);
    }

    [Fact]
    public void SqlWhere_Empty_RendersNothing() {
        var where = new SqlWhere();

        where.IsEmpty.Should().BeTrue();
        var sql = new SqlStatement($"SELECT * FROM t {where}");
        sql.Text.Should().Be("SELECT * FROM t ");
        sql.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void SqlWhere_AccumulatesParenthesizedAndJoinedPredicates() {
        var where = new SqlWhere();
        where.And($"device_id = {"edge-1"}");
        where.And($"value > {10.0}");

        where.IsEmpty.Should().BeFalse();
        var sql = new SqlStatement($"SELECT * FROM readings {where} ORDER BY recorded_at");

        sql.Text.Should().Be("SELECT * FROM readings WHERE (device_id = @p0) AND (value > @p1) ORDER BY recorded_at");
        sql.ParameterValues.Should().Equal("edge-1", 10.0);
    }

    [Fact]
    public void SqlWhere_ReusableAcrossPageAndCountQueries() {
        // The same accumulator drives a page query and its count(*) companion — no WHERE duplication.
        var where = new SqlWhere();
        where.And($"status = {"active"}");

        var page = new SqlStatement($"SELECT * FROM t {where} LIMIT {50}");
        var count = new SqlStatement($"SELECT count(*) FROM t {where}");

        page.Text.Should().Be("SELECT * FROM t WHERE (status = @p0) LIMIT @p1");
        count.Text.Should().Be("SELECT count(*) FROM t WHERE (status = @p0)");
    }
}
