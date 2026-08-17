using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.EntityFrameworkCore;

/// <summary>
/// Round-trips the opt-in value conventions against real SQLite (in-process, no Docker, so this suite always
/// runs). Model-level assertions prove what the model claims; this proves the claim survives a real write and a
/// real read — including what the column literally holds, and that the collection comparer makes an in-place
/// edit visible to change detection.
/// </summary>
public sealed class ElarionValueConventionsSqliteTests : IAsyncLifetime {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "elarion_sqlite_conventions_" + Guid.CreateVersion7().ToString("N") + ".db");

    private string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ConnectionString;

    public async ValueTask InitializeAsync() {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() {
        try {
            File.Delete(_path);
        }
        catch (IOException) {
            // Best-effort temp cleanup; the OS reclaims it regardless.
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task StoresEnumsAsText_AndStringArraysAsJson() {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();

        await using (var context = CreateContext()) {
            context.Widgets.Add(new ConventionWidget {
                Id = id,
                Name = "gauge",
                Count = 3,
                Status = ConventionStatus.Active,
                OptionalStatus = ConventionStatus.Retired,
                OrdinalStatus = ConventionStatus.Retired,
                CodedStatus = ConventionStatus.Active,
                Tags = ["north", "south"]
            });
            await context.SaveChangesAsync(ct);
        }

        // What the columns literally hold: the enum reads as its name, the array as JSON — the whole point of
        // both conventions is a schema a human can read and migrate.
        await using (var connection = new SqliteConnection(ConnectionString)) {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            // Filtered by Name, not Id: how the provider encodes a Guid key is not what this test is about.
            command.CommandText =
                "SELECT Status, OptionalStatus, OrdinalStatus, CodedStatus, Tags FROM Widgets WHERE Name = $name";
            command.Parameters.AddWithValue("$name", "gauge");
            await using var reader = await command.ExecuteReaderAsync(ct);

            (await reader.ReadAsync(ct)).Should().BeTrue();
            reader.GetString(0).Should().Be("Active");
            reader.GetString(1).Should().Be("Retired");
            reader.GetInt32(2).Should().Be((int)ConventionStatus.Retired); // explicit HasConversion<int>() wins
            reader.GetString(3).Should().Be("A"); // explicit converter instance wins
            reader.GetString(4).Should().Be("[\"north\",\"south\"]");
        }

        await using (var context = CreateContext()) {
            var stored = await context.Widgets.SingleAsync(widget => widget.Id == id, ct);
            stored.Status.Should().Be(ConventionStatus.Active);
            stored.OptionalStatus.Should().Be(ConventionStatus.Retired);
            stored.OrdinalStatus.Should().Be(ConventionStatus.Retired);
            stored.CodedStatus.Should().Be(ConventionStatus.Active);
            stored.Tags.Should().Equal("north", "south");
        }
    }

    [Fact]
    public async Task DetectsAnInPlaceEditOfAConvertedArray() {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();

        await using (var context = CreateContext()) {
            context.Widgets.Add(new ConventionWidget { Id = id, Name = "probe", Tags = ["one", "two"] });
            await context.SaveChangesAsync(ct);
        }

        await using (var context = CreateContext()) {
            var stored = await context.Widgets.SingleAsync(widget => widget.Id == id, ct);
            // Mutating in place — not reassigning the property — is exactly what a reference-equality comparer
            // would miss, so this is the regression the blessed comparer exists to prevent.
            stored.Tags[1] = "three";
            (await context.SaveChangesAsync(ct)).Should().Be(1);
        }

        await using (var context = CreateContext()) {
            var stored = await context.Widgets.SingleAsync(widget => widget.Id == id, ct);
            stored.Tags.Should().Equal("one", "three");
        }
    }

    [Fact]
    public async Task ReadsAnEmptyArrayColumn() {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();

        await using (var context = CreateContext()) {
            context.Widgets.Add(new ConventionWidget { Id = id, Name = "bare", Tags = [] });
            await context.SaveChangesAsync(ct);
        }

        await using (var context = CreateContext()) {
            var stored = await context.Widgets.SingleAsync(widget => widget.Id == id, ct);
            stored.Tags.Should().BeEmpty();
            stored.OptionalStatus.Should().BeNull();
        }
    }

    [Fact]
    public async Task NullColumnReadsAsNull_WhileAnEmptyStringColumnReadsAsEmpty() {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.CreateVersion7();

        await using (var context = CreateContext()) {
            // A null array property: EF builds the converter with convertsNulls: false, so it short circuits
            // and writes SQL NULL rather than the JSON "null" the converter would produce.
            context.Widgets.Add(new ConventionWidget {
                Id = id, Name = "nulls", Tags = ["kept"], OptionalTags = null
            });
            await context.SaveChangesAsync(ct);
        }

        await using (var connection = new SqliteConnection(ConnectionString)) {
            await connection.OpenAsync(ct);

            await using (var probe = connection.CreateCommand()) {
                probe.CommandText = "SELECT OptionalTags IS NULL FROM Widgets WHERE Name = $name";
                probe.Parameters.AddWithValue("$name", "nulls");
                (await probe.ExecuteScalarAsync(ct)).Should().Be(1L);
            }

            // The empty string is what an AddColumn migration with a "" default leaves in existing rows — the
            // one non-JSON value the converter genuinely has to absorb.
            await using var backfill = connection.CreateCommand();
            backfill.CommandText = "UPDATE Widgets SET Tags = '' WHERE Name = $name";
            backfill.Parameters.AddWithValue("$name", "nulls");
            (await backfill.ExecuteNonQueryAsync(ct)).Should().Be(1);
        }

        await using (var context = CreateContext()) {
            var stored = await context.Widgets.SingleAsync(widget => widget.Id == id, ct);

            // NULL is not an empty array: the converter never runs for it, so the property reads back null.
            stored.OptionalTags.Should().BeNull();
            stored.Tags.Should().BeEmpty();
        }
    }

    private ConventionsDbContext CreateContext() {
        return new ConventionsDbContext(new DbContextOptionsBuilder<ConventionsDbContext>()
            .UseSqlite(ConnectionString)
            .Options);
    }
}
