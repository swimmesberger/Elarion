using AwesomeAssertions;
using Elarion.EntityFrameworkCore.BulkOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.BulkOperations;

/// <summary>
/// End-to-end tests for <c>ExecuteInsertAsync</c> over PostgreSQL binary COPY: value shapes round-trip,
/// the insert joins the caller's ambient transaction, store-generated columns stay database-filled, and
/// unsupported shapes fail loud before touching the database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlBulkInsertIntegrationTests(PostgreSqlBulkInsertFixture fixture)
    : IClassFixture<PostgreSqlBulkInsertFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset FixedInstant = new(2026, 7, 11, 12, 30, 45, 123, TimeSpan.Zero);

    private static BulkOrder NewOrder(int index) {
        return new BulkOrder {
            Id = Guid.CreateVersion7(),
            Name = $"order-{index}",
            Description = index % 3 == 0 ? null : $"description {index}",
            Quantity = index,
            Price = 10.25m + index,
            CreatedAt = FixedInstant.AddSeconds(index),
            Active = index % 2 == 0,
            Status = (BulkOrderStatus)(index % 3),
            Rating = index % 4 == 0 ? null : index % 5,
            Tags = index % 5 == 0 ? null : ["bulk", $"t{index}"],
            Payload = index % 6 == 0 ? null : [1, 2, (byte)index]
        };
    }

    [Fact]
    public async Task ExecuteInsert_RoundTripsAllValueShapes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var orders = Enumerable.Range(0, 25).Select(NewOrder).OrderBy(o => o.Id).ToList();
        await using var context = fixture.CreateContext();
        var written = await context.Orders.ExecuteInsertAsync(orders, cancellationToken: Ct);

        written.Should().Be(orders.Count);
        await using var verify = fixture.CreateContext();
        var read = await verify.Orders.AsNoTracking()
            .Where(o => orders.Select(x => x.Id).Contains(o.Id))
            .OrderBy(o => o.Id)
            .ToListAsync(Ct);
        read.Should().BeEquivalentTo(orders, options => options.WithStrictOrdering());
        context.ChangeTracker.Entries().Should().BeEmpty("bulk insert is non-tracking");
    }

    [Fact]
    public async Task ExecuteInsert_ManyRows_ReturnsCount() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var orders = Enumerable.Range(0, 10_000).Select(NewOrder).ToList();
        await using var context = fixture.CreateContext();
        var written = await context.Orders.ExecuteInsertAsync(orders, cancellationToken: Ct);

        written.Should().Be(10_000);
    }

    [Fact]
    public async Task ExecuteInsert_JoinsAmbientTransaction_RollbackDiscards() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var orders = Enumerable.Range(0, 10).Select(NewOrder).ToList();
        await using var context = fixture.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct)) {
            await context.Orders.ExecuteInsertAsync(orders, cancellationToken: Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verify = fixture.CreateContext();
        (await verify.Orders.CountAsync(o => orders.Select(x => x.Id).Contains(o.Id), Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInsert_JoinsAmbientTransaction_CommitsWithBusinessWrites() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var orders = Enumerable.Range(0, 10).Select(NewOrder).ToList();
        var tracked = NewOrder(999);
        await using var context = fixture.CreateContext();
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct)) {
            context.Orders.Add(tracked);
            await context.SaveChangesAsync(Ct);
            await context.Orders.ExecuteInsertAsync(orders, cancellationToken: Ct);
            await transaction.CommitAsync(Ct);
        }

        var expected = orders.Select(o => o.Id).Append(tracked.Id).ToList();
        await using var verify = fixture.CreateContext();
        (await verify.Orders.CountAsync(o => expected.Contains(o.Id), Ct)).Should().Be(expected.Count);
    }

    [Fact]
    public async Task ExecuteInsert_StoreGeneratedColumns_AreFilledByDatabase() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var events = Enumerable.Range(0, 5).Select(i => new BulkAuditEvent { Message = $"m{i}" }).ToList();
        await using var context = fixture.CreateContext();
        await context.AuditEvents.ExecuteInsertAsync(events, cancellationToken: Ct);

        await using var verify = fixture.CreateContext();
        var read = await verify.AuditEvents.AsNoTracking().Where(e => e.Message.StartsWith("m")).ToListAsync(Ct);
        read.Should().HaveCount(events.Count);
        read.Should().OnlyContain(e => e.Id > 0, "the identity column is database-assigned");
        read.Should().OnlyContain(e => e.RecordedAt > DateTimeOffset.MinValue,
            "the now() default is database-assigned");
    }

    [Fact]
    public async Task ExecuteInsert_StreamsAsyncEnumerable() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var ids = new List<Guid>();

        async IAsyncEnumerable<BulkOrder> StreamAsync() {
            for (var i = 0; i < 100; i++) {
                var order = NewOrder(i);
                ids.Add(order.Id);
                yield return order;
                if (i % 25 == 0) await Task.Yield();
            }
        }

        await using var context = fixture.CreateContext();
        var written = await context.Orders.ExecuteInsertAsync(StreamAsync(), cancellationToken: Ct);

        written.Should().Be(100);
        await using var verify = fixture.CreateContext();
        (await verify.Orders.CountAsync(o => ids.Contains(o.Id), Ct)).Should().Be(100);
    }

    [Fact]
    public async Task ExecuteInsert_EmptyInput_ReturnsZeroWithoutTouchingTheDatabase() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var context = fixture.CreateContext();
        (await context.Orders.ExecuteInsertAsync([], cancellationToken: Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInsert_WritesTphDiscriminators() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var dogs = new List<BulkDog> {
            new() { Id = Guid.CreateVersion7(), Name = "rex", FavoriteToy = "ball" },
            new() { Id = Guid.CreateVersion7(), Name = "bello", FavoriteToy = null }
        };
        var cats = new List<BulkCat> { new() { Id = Guid.CreateVersion7(), Name = "mio", Indoor = true } };

        await using var context = fixture.CreateContext();
        await context.Set<BulkDog>().ExecuteInsertAsync(dogs, cancellationToken: Ct);
        await context.Set<BulkCat>().ExecuteInsertAsync(cats, cancellationToken: Ct);

        await using var verify = fixture.CreateContext();
        var readDogs = await verify.Animals.AsNoTracking().OfType<BulkDog>().OrderBy(d => d.Name).ToListAsync(Ct);
        var readCats = await verify.Animals.AsNoTracking().OfType<BulkCat>().ToListAsync(Ct);
        readDogs.Should().BeEquivalentTo(dogs);
        readCats.Should().BeEquivalentTo(cats);
    }

    [Fact]
    public async Task ExecuteInsert_DerivedInstanceInBaseSet_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        await using var context = fixture.CreateContext();
        var act = async () => await context.Animals.ExecuteInsertAsync(
            [new BulkDog { Id = Guid.CreateVersion7(), Name = "sneaky", FavoriteToy = "sock" }],
            cancellationToken: Ct);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*derived type*");
    }

    [Fact]
    public async Task ExecuteInsert_RoundTripsComplexProperties() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var shipments = Enumerable.Range(0, 20).Select(i => new BulkShipment {
            Id = Guid.CreateVersion7(),
            Reference = $"ship-{i}",
            Address = new ShippingAddress {
                Street = $"Street {i}",
                Note = i % 2 == 0 ? null : $"note {i}",
                Geo = new GeoPoint { Latitude = 47.0 + i, Longitude = 13.0 - i }
            }
        }).ToList();

        await using var context = fixture.CreateContext();
        var written = await context.Shipments.ExecuteInsertAsync(shipments, cancellationToken: Ct);

        written.Should().Be(shipments.Count);
        await using var verify = fixture.CreateContext();
        var read = await verify.Shipments.AsNoTracking()
            .Where(s => shipments.Select(x => x.Id).Contains(s.Id))
            .ToListAsync(Ct);
        read.Should().BeEquivalentTo(shipments);
    }

    [Fact]
    public async Task ExecuteInsert_OnConflictDoNothing_SkipsExistingRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var existing = Enumerable.Range(0, 5).Select(i => new BulkCounter {
            Id = Guid.CreateVersion7(), Key = $"dn-{Guid.CreateVersion7():N}-{i}", Count = i
        }).ToList();
        await using var context = fixture.CreateContext();
        await context.Counters.ExecuteInsertAsync(existing, cancellationToken: Ct);

        var retry = existing
            .Select(c => new BulkCounter { Id = c.Id, Key = c.Key, Count = c.Count + 100 })
            .Concat(Enumerable.Range(0, 3).Select(i => new BulkCounter {
                Id = Guid.CreateVersion7(), Key = $"dn-new-{Guid.CreateVersion7():N}-{i}", Count = i
            }))
            .ToList();
        var written = await context.Counters.ExecuteInsertAsync(
            retry, new BulkInsertOptions { OnConflict = BulkInsertConflictBehavior.DoNothing }, Ct);

        written.Should().Be(3, "the five conflicting rows are skipped");
        await using var verify = fixture.CreateContext();
        var kept = await verify.Counters.AsNoTracking()
            .Where(c => existing.Select(x => x.Id).Contains(c.Id)).ToListAsync(Ct);
        kept.Should().BeEquivalentTo(existing, "DoNothing must leave existing rows untouched");
    }

    [Fact]
    public async Task ExecuteInsert_OnConflictUpdate_OverwritesExistingRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var existing = Enumerable.Range(0, 5).Select(i => new BulkCounter {
            Id = Guid.CreateVersion7(), Key = $"up-{Guid.CreateVersion7():N}-{i}", Count = i
        }).ToList();
        await using var context = fixture.CreateContext();
        await context.Counters.ExecuteInsertAsync(existing, cancellationToken: Ct);

        var upsert = existing
            .Select(c => new BulkCounter { Id = c.Id, Key = c.Key, Count = c.Count + 100 })
            .Concat([
                new BulkCounter { Id = Guid.CreateVersion7(), Key = $"up-new-{Guid.CreateVersion7():N}", Count = 1 }
            ])
            .ToList();
        var written = await context.Counters.ExecuteInsertAsync(
            upsert, new BulkInsertOptions { OnConflict = BulkInsertConflictBehavior.Update }, Ct);

        written.Should().Be(6, "five rows update and one inserts");
        await using var verify = fixture.CreateContext();
        var read = await verify.Counters.AsNoTracking()
            .Where(c => upsert.Select(x => x.Id).Contains(c.Id)).ToListAsync(Ct);
        read.Should().BeEquivalentTo(upsert);
    }

    [Fact]
    public async Task ExecuteInsert_OnConflictUpdate_HonorsAlternateUniqueTarget() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var key = $"alt-{Guid.CreateVersion7():N}";
        await using var context = fixture.CreateContext();
        await context.Counters.ExecuteInsertAsync(
            [new BulkCounter { Id = Guid.CreateVersion7(), Key = key, Count = 1 }], cancellationToken: Ct);

        var written = await context.Counters.ExecuteInsertAsync(
            [new BulkCounter { Id = Guid.CreateVersion7(), Key = key, Count = 42 }],
            new BulkInsertOptions {
                OnConflict = BulkInsertConflictBehavior.Update,
                ConflictProperties = [nameof(BulkCounter.Key)]
            },
            Ct);

        written.Should().Be(1);
        await using var verify = fixture.CreateContext();
        var read = await verify.Counters.AsNoTracking().SingleAsync(c => c.Key == key, Ct);
        read.Count.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteInsert_Upsert_JoinsAmbientTransaction_RollbackDiscards() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var counter = new BulkCounter { Id = Guid.CreateVersion7(), Key = $"tx-{Guid.CreateVersion7():N}", Count = 1 };
        await using var context = fixture.CreateContext();
        await context.Counters.ExecuteInsertAsync([counter], cancellationToken: Ct);

        await using (var transaction = await context.Database.BeginTransactionAsync(Ct)) {
            await context.Counters.ExecuteInsertAsync(
                [new BulkCounter { Id = counter.Id, Key = counter.Key, Count = 99 }],
                new BulkInsertOptions { OnConflict = BulkInsertConflictBehavior.Update },
                Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verify = fixture.CreateContext();
        (await verify.Counters.AsNoTracking().SingleAsync(c => c.Id == counter.Id, Ct)).Count.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteInsert_FailedCopy_LeavesNoPartialRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);

        var duplicate = Guid.CreateVersion7();
        var orders = Enumerable.Range(0, 10).Select(NewOrder).ToList();
        orders[7].Id = orders[2].Id = duplicate;

        await using var context = fixture.CreateContext();
        var act = async () => await context.Orders.ExecuteInsertAsync(orders, cancellationToken: Ct);
        await act.Should().ThrowAsync<Exception>("COPY aborts on the primary-key violation");

        await using var verify = fixture.CreateContext();
        (await verify.Orders.CountAsync(o => o.Id == duplicate, Ct)).Should().Be(0, "COPY is all-or-nothing");
    }
}
