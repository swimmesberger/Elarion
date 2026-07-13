using AwesomeAssertions;
using Elarion.Migrations.PostgreSql;
using Xunit;

namespace Elarion.Tests.Migrations;

public sealed class SqlStatementSplitterTests {
    [Fact]
    public void SplitsOnTopLevelSemicolons() {
        var statements = SqlStatementSplitter.Split("CREATE TABLE a (id int);\nCREATE TABLE b (id int);\n");
        statements.Should().Equal("CREATE TABLE a (id int)", "CREATE TABLE b (id int)");
    }

    [Fact]
    public void KeepsTrailingStatementWithoutSemicolon() {
        SqlStatementSplitter.Split("SELECT 1").Should().Equal("SELECT 1");
    }

    [Fact]
    public void IgnoresSemicolonsInStringsCommentsAndQuotedIdentifiers() {
        var sql = """
            INSERT INTO t (a, b) VALUES ('x;y', E'a\';b');
            -- a comment; with a semicolon
            /* block; comment /* nested; */ still; */
            UPDATE "wei;rd" SET a = 'it''s;fine';
            """;
        var statements = SqlStatementSplitter.Split(sql);
        statements.Should().HaveCount(2);
        statements[0].Should().StartWith("INSERT INTO t");
        statements[1].Should().EndWith("'it''s;fine'");
    }

    [Fact]
    public void IgnoresSemicolonsInDollarQuotedBodies() {
        var sql = """
            CREATE FUNCTION f() RETURNS void AS $body$
            BEGIN
                PERFORM 1;
                PERFORM 2;
            END;
            $body$ LANGUAGE plpgsql;
            SELECT f();
            """;
        var statements = SqlStatementSplitter.Split(sql);
        statements.Should().HaveCount(2);
        statements[0].Should().Contain("PERFORM 2;");
        statements[1].Should().Be("SELECT f()");
    }

    [Fact]
    public void SkipsCommentOnlyAndEmptyChunks() {
        var sql = """
            -- leading comment
            SELECT 1;
            ;
            -- trailing comment only
            """;
        SqlStatementSplitter.Split(sql).Should().HaveCount(1);
    }

    [Fact]
    public void DollarParameterIsNotADollarQuote() {
        SqlStatementSplitter.Split("SELECT $1; SELECT $2").Should().HaveCount(2);
    }

    [Fact]
    public void DollarInsideAnIdentifierIsNotADollarQuote() {
        // x$$ is a legal PostgreSQL identifier; the semicolon after it must still split.
        SqlStatementSplitter.Split("SELECT x$$ FROM t; SELECT 1").Should().HaveCount(2);
    }
}
