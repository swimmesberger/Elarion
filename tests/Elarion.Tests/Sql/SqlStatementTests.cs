using AwesomeAssertions;
using Elarion.Sql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

public sealed class SqlStatementTests {
    [Fact]
    public void ScalarValues_BecomeSequentialParameters() {
        var status = "active";
        var id = 42;

        var sql = SqlStatement.Of($"UPDATE orders SET status = {status} WHERE id = {id}");

        sql.Text.Should().Be("UPDATE orders SET status = @p0 WHERE id = @p1");
        sql.ParameterValues.Should().Equal("active", 42);
    }

    [Fact]
    public void NullValue_BindsAsParameter() {
        string? note = null;

        var sql = SqlStatement.Of($"UPDATE orders SET note = {note}");

        sql.Text.Should().Be("UPDATE orders SET note = @p0");
        sql.ParameterValues.Should().Equal(new object?[] { null });
    }

    [Fact]
    public void Collection_ExpandsToParameterList() {
        int[] ids = [1, 2, 3];

        var sql = SqlStatement.Of($"SELECT * FROM orders WHERE id IN {ids}");

        sql.Text.Should().Be("SELECT * FROM orders WHERE id IN (@p0, @p1, @p2)");
        sql.ParameterValues.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void EmptyCollection_FailsLoud() {
        int[] ids = [];

        var sql = SqlStatement.Of($"SELECT * FROM orders WHERE id IN {ids}");

        // "(NULL)" would silently flip NOT IN to match nothing, and an untyped never-matching
        // subquery fails PostgreSQL's type inference — so the empty set fails at build time.
        var act = () => sql.Text;
        act.Should().Throw<InvalidOperationException>().WithMessage("*empty collection*");
    }

    [Fact]
    public void StringAndByteArray_StayScalar() {
        var name = "abc";
        var payload = new byte[] { 1, 2 };

        var sql = SqlStatement.Of($"SELECT * FROM t WHERE name = {name} AND payload = {payload}");

        sql.Text.Should().Be("SELECT * FROM t WHERE name = @p0 AND payload = @p1");
        sql.ParameterValues.Should().HaveCount(2);
    }

    [Fact]
    public void RawFormat_SplicesVerbatim() {
        var table = "orders";

        var sql = SqlStatement.Of($"SELECT count(*) FROM {table:raw}");

        sql.Text.Should().Be("SELECT count(*) FROM orders");
        sql.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void UnknownFormat_Throws() {
        var act = () => SqlStatement.Of($"SELECT {1:d4}");

        act.Should().Throw<FormatException>().WithMessage("*'d4'*");
    }

    [Fact]
    public void Fragment_SplicesWithRenumberedParameters() {
        var status = "open";
        var limit = 10;
        var where = SqlStatement.Of($"WHERE status = {status}");

        var sql = SqlStatement.Of($"SELECT * FROM orders {where} LIMIT {limit}");

        sql.Text.Should().Be("SELECT * FROM orders WHERE status = @p0 LIMIT @p1");
        sql.ParameterValues.Should().Equal("open", 10);
    }

    [Fact]
    public void NestedFragments_ComposeRecursively() {
        var inner = SqlStatement.Of($"price > {5m}");
        var outer = SqlStatement.Of($"WHERE {inner} AND qty < {7}");

        var sql = SqlStatement.Of($"SELECT * FROM t {outer}");

        sql.Text.Should().Be("SELECT * FROM t WHERE price > @p0 AND qty < @p1");
        sql.ParameterValues.Should().Equal(5m, 7);
    }

    [Fact]
    public void FragmentReuse_RebindsParametersPerStatement() {
        var shared = SqlStatement.Of($"status = {"open"}");

        var first = SqlStatement.Of($"SELECT 1 WHERE {shared}");
        var second = SqlStatement.Of($"SELECT 2 WHERE {shared} AND id = {3}");

        first.Text.Should().Be("SELECT 1 WHERE status = @p0");
        second.Text.Should().Be("SELECT 2 WHERE status = @p0 AND id = @p1");
        second.ParameterValues.Should().Equal("open", 3);
    }

    [Fact]
    public void SqlTypedValue_SplicesAsFragmentViaTypedOverload() {
        SqlStatement orderBy = SqlStatement.Raw("ORDER BY created_at DESC");

        var sql = SqlStatement.Of($"SELECT * FROM orders {orderBy}");

        sql.Text.Should().Be("SELECT * FROM orders ORDER BY created_at DESC");
    }

    [Fact]
    public void PlainText_HasNoParameters() {
        var sql = SqlStatement.Raw("SELECT 1");

        sql.Text.Should().Be("SELECT 1");
        sql.ParameterValues.Should().BeEmpty();
    }

    [Fact]
    public void ConstantOnlyInterpolation_StillParameterizes() {
        // A constant-only interpolated string is a C# constant expression; with a string overload in
        // play, overload resolution would prefer it and splice the hole as text. The API deliberately
        // has no string overloads, so even constants bind as parameters.
        const string status = "open";

        var sql = SqlStatement.Of($"status = {status}");

        sql.Text.Should().Be("status = @p0");
        sql.ParameterValues.Should().Equal("open");
    }
}
