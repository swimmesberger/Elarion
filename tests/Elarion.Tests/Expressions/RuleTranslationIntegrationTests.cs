using System.Linq.Expressions;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Elarion.Expressions;
using Elarion.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Expressions;

/// <summary>
/// Pins, against a real PostgreSQL instance, that rules composed with <c>Invoke</c> translate to SQL after
/// <c>Expand()</c>, that the SQL computes what <c>rule.Invoke</c> computes in memory, that captured values become
/// parameters, and that the join-once recipe evaluates an aggregate once. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RuleTranslationIntegrationTests(PostgreSqlRulesFixture fixture)
    : IClassFixture<PostgreSqlRulesFixture> {
    private static readonly Rule<RuleInvoice, decimal> Paid =
        Rule.Create((RuleInvoice invoice) => invoice.Payments.Sum(p => p.Amount));

    private static readonly Rule<RuleInvoice, decimal> OpenAmount =
        Rule.Create((RuleInvoice invoice) => invoice.Gross - Paid.Invoke(invoice));

    private static readonly Rule<RuleInvoice, bool> IsSettled =
        Rule.Create((RuleInvoice invoice) => OpenAmount.Invoke(invoice) <= 0);

    // The same rules over the joined aggregate: the paid sum is an input, computed once per row by the join.
    private static readonly Rule<RuleInvoice, decimal, decimal> OpenGiven =
        Rule.Create((RuleInvoice invoice, decimal paid) => invoice.Gross - paid);

    private static readonly Rule<RuleInvoice, decimal, bool> IsPartiallyPaid =
        Rule.Create((RuleInvoice invoice, decimal paid) =>
            OpenGiven.Invoke(invoice, paid) > 0 && OpenGiven.Invoke(invoice, paid) < invoice.Gross);

    private static readonly Rule<RuleBooking, BookingWindow, bool> InWindow =
        Rule.Create((RuleBooking booking, BookingWindow window) =>
            booking.Account == window.Account && booking.BookedOn >= window.From && booking.BookedOn <= window.To);

    private static readonly Rule<RuleBooking, decimal, bool> AmountAbove =
        Rule.Create((RuleBooking booking, decimal minimum) => booking.Amount > minimum);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RuleInAFilter_TranslatesAfterExpand_AndMatchesInMemory() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();

        var query = db.Invoices
            .Where(invoice => !IsSettled.Invoke(invoice))
            .OrderBy(invoice => invoice.Number)
            .Select(invoice => invoice.Number)
            .Expand();
        var numbers = await query.ToListAsync(Ct);

        query.ToQueryString().Should().ContainEquivalentOf("sum(");
        var loaded = await LoadInvoicesAsync(db);
        numbers.Should().Equal(loaded.Where(invoice => !IsSettled.Invoke(invoice)).Select(invoice => invoice.Number));
        numbers.Should().Equal("A-2", "A-3", "A-5");
    }

    [Fact]
    public async Task RuleInAProjection_TranslatesAfterExpand_AndMatchesInMemory() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();

        var query = db.Invoices
            .OrderBy(invoice => invoice.Number)
            .Select(invoice => new OpenInvoiceRow(invoice.Number, OpenAmount.Invoke(invoice), IsSettled.Invoke(invoice)))
            .Expand();
        var rows = await query.ToListAsync(Ct);

        query.ToQueryString().Should().ContainEquivalentOf("sum(");
        var loaded = await LoadInvoicesAsync(db);
        rows.Should().Equal(loaded.Select(invoice =>
            new OpenInvoiceRow(invoice.Number, OpenAmount.Invoke(invoice), IsSettled.Invoke(invoice))));
        rows.Select(row => row.Open).Should().Equal(0m, 60m, 50m, 0m, 150m);
    }

    [Fact]
    public async Task ForgottenExpand_InAFilter_FailsTranslationNamingInvoke() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();

        var query = db.Invoices.Where(invoice => !IsSettled.Invoke(invoice));
        var load = async () => await query.ToListAsync(Ct);

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Invoke*could not be translated*");
    }

    [Fact]
    public async Task ForgottenExpand_RuleInAStaticFieldInTheFinalProjection_FailsNamingInvoke() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();

        var query = db.Invoices.Select(invoice => OpenAmount.Invoke(invoice));
        var load = async () => await query.ToListAsync(Ct);

        (await load.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*client projection*Rule*Invoke*");
    }

    [Fact]
    public async Task ForgottenExpand_RuleInACapturedLocalInTheFinalProjection_RunsOnTheClientWithoutTheNavigation() {
        // The hazard the documentation warns about: EF evaluates an untranslatable final projection on the client,
        // where the entity's Payments were never loaded, so the open amount silently equals the gross amount.
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();
        var openAmount = OpenAmount;

        var open = await db.Invoices
            .OrderBy(invoice => invoice.Number)
            .Select(invoice => openAmount.Invoke(invoice))
            .ToListAsync(Ct);

        open.Should().Equal(100m, 100m, 50m, 80m, 200m);
    }

    [Fact]
    public async Task CapturedValue_IsSentAsAParameter_WhereAConstantIsALiteral() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();
        var booking = Expression.Parameter(typeof(RuleBooking), "booking");
        var captured = Expression.Lambda<Func<RuleBooking, bool>>(
            Expr.Inline(AmountAbove.Expression, booking, Expr.Captured(1234.5m)), booking);
        var constant = Expression.Lambda<Func<RuleBooking, bool>>(
            Expr.Inline(AmountAbove.Expression, booking, Expression.Constant(1234.5m)), booking);

        var capturedSql = db.Bookings.Where(captured).ToQueryString();
        var constantSql = db.Bookings.Where(constant).ToQueryString();
        var ids = await db.Bookings.Where(captured).OrderBy(b => b.Id).Select(b => b.Id).ToListAsync(Ct);

        Statement(capturedSql).Should().NotContain("1234.5");
        ParameterDeclarations(capturedSql).Should().ContainSingle().Which.Should().Contain("1234.5");
        Statement(constantSql).Should().Contain("1234.5");
        var loaded = await db.Bookings.OrderBy(b => b.Id).ToListAsync(Ct);
        ids.Should().Equal(loaded.Where(b => AmountAbove.Invoke(b, 1234.5m)).Select(b => b.Id)).And.NotBeEmpty();
    }

    [Fact]
    public async Task AnyOfPerAccountWindows_TranslatesWithParameters_AndMatchesInMemory() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();
        BookingWindow[] windows = [
            new("acc-1", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)),
            new("acc-2", new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)),
        ];
        var inAnyWindow = Expr.AnyOf(windows.Select(window =>
            (Expression<Func<RuleBooking, bool>>)(booking => InWindow.Invoke(booking, window))));

        var query = db.Bookings.Where(inAnyWindow).OrderBy(booking => booking.Id).Select(booking => booking.Id).Expand();
        var sql = query.ToQueryString();
        var ids = await query.ToListAsync(Ct);

        // No literal at all: every account and date of every window is a parameter.
        Statement(sql).Should().NotContain("'");
        ParameterDeclarations(sql).Should().HaveCount(6);
        var loaded = await db.Bookings.OrderBy(booking => booking.Id).ToListAsync(Ct);
        ids.Should().Equal(loaded.Where(inAnyWindow.Invoke).Select(booking => booking.Id));
        ids.Should().Equal(loaded.Where(b => b.Note == "in window").Select(b => b.Id)).And.HaveCount(3);
    }

    [Fact]
    public async Task AggregateJoinedOnce_IsSummedOnce_WhereTheCorrelatedRuleSumsAtEveryUse() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var db = fixture.CreateContext();

        var paidByInvoice = db.Payments
            .GroupBy(payment => payment.InvoiceId)
            .Select(group => new { InvoiceId = group.Key, Paid = group.Sum(payment => payment.Amount) });
        var joined = db.Invoices
            .LeftJoin(paidByInvoice, invoice => invoice.Id, paid => paid.InvoiceId,
                (invoice, paid) => new { Invoice = invoice, Paid = (decimal?)paid!.Paid ?? 0m })
            .Where(row => IsPartiallyPaid.Invoke(row.Invoice, row.Paid))
            .OrderBy(row => row.Invoice.Number)
            .Select(row => new OpenInvoiceRow(row.Invoice.Number, OpenGiven.Invoke(row.Invoice, row.Paid), false))
            .Expand();
        var correlated = db.Invoices
            .Where(invoice => OpenAmount.Invoke(invoice) > 0 && OpenAmount.Invoke(invoice) < invoice.Gross)
            .OrderBy(invoice => invoice.Number)
            .Select(invoice => new OpenInvoiceRow(invoice.Number, OpenAmount.Invoke(invoice), false))
            .Expand();

        var rows = await joined.ToListAsync(Ct);

        CountSums(joined.ToQueryString()).Should().Be(1);
        CountSums(correlated.ToQueryString()).Should().Be(3);
        rows.Should().Equal(await correlated.ToListAsync(Ct));
        var loaded = await LoadInvoicesAsync(db);
        rows.Should().Equal(loaded
            .Where(invoice => IsPartiallyPaid.Invoke(invoice, Paid.Invoke(invoice)))
            .Select(invoice => new OpenInvoiceRow(invoice.Number, OpenGiven.Invoke(invoice, Paid.Invoke(invoice)), false)));
        rows.Select(row => row.Number).Should().Equal("A-2", "A-5");
    }

    private static async Task<List<RuleInvoice>> LoadInvoicesAsync(RulesDbContext db) {
        return await db.Invoices.Include(invoice => invoice.Payments).OrderBy(invoice => invoice.Number).ToListAsync(Ct);
    }

    private static int CountSums(string sql) {
        return Regex.Matches(sql, @"\bsum\(", RegexOptions.IgnoreCase).Count;
    }

    // ToQueryString puts one "-- @name='value'" line per parameter above the statement.
    private static string[] ParameterDeclarations(string sql) {
        return sql.Split('\n').Where(line => line.StartsWith("-- @", StringComparison.Ordinal)).ToArray();
    }

    private static string Statement(string sql) {
        return string.Join('\n', sql.Split('\n').Where(line => !line.StartsWith("--", StringComparison.Ordinal)));
    }
}

public sealed record OpenInvoiceRow(string Number, decimal Open, bool Settled);

public sealed record BookingWindow(string Account, DateOnly From, DateOnly To);

public sealed class RuleInvoice {
    public Guid Id { get; set; }
    public required string Number { get; set; }
    public decimal Gross { get; set; }
    public List<RulePayment> Payments { get; set; } = [];
}

public sealed class RulePayment {
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
}

public sealed class RuleBooking {
    public Guid Id { get; set; }
    public required string Account { get; set; }
    public DateOnly BookedOn { get; set; }
    public decimal Amount { get; set; }
    public required string Note { get; set; }
}

public sealed class RulesDbContext(DbContextOptions<RulesDbContext> options) : DbContext(options) {
    public DbSet<RuleInvoice> Invoices => Set<RuleInvoice>();
    public DbSet<RulePayment> Payments => Set<RulePayment>();
    public DbSet<RuleBooking> Bookings => Set<RuleBooking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<RuleInvoice>(b => {
            b.ToTable("rule_invoices");
            b.Property(i => i.Id).ValueGeneratedNever();
            b.HasMany(i => i.Payments).WithOne().HasForeignKey(p => p.InvoiceId);
        });
        modelBuilder.Entity<RulePayment>(b => {
            b.ToTable("rule_payments");
            b.Property(p => p.Id).ValueGeneratedNever();
        });
        modelBuilder.Entity<RuleBooking>(b => {
            b.ToTable("rule_bookings");
            b.Property(p => p.Id).ValueGeneratedNever();
        });
    }
}

/// <summary>
/// Starts a disposable PostgreSQL container, creates the schema and seeds the invoices and bookings the rule tests
/// read. When Docker is not available the fixture records a skip reason instead of failing.
/// </summary>
public sealed class PostgreSqlRulesFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;
    private string _connectionString = "";

    /// <summary>Gets a value indicating whether the container started and the data is seeded.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Gets the reason the integration tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    public string SkipReason { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            // Build() validates the Docker endpoint, so it must run inside the guard too.
            container = await TestContainerLifecycle.Shared.StartAsync(() =>
                new PostgreSqlBuilder("postgres:17-alpine").Build());
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        _connectionString = container.GetConnectionString();
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        db.Invoices.AddRange(
            Invoice("A-1", 100m, 100m),
            Invoice("A-2", 100m, 40m),
            Invoice("A-3", 50m),
            Invoice("A-4", 80m, 50m, 30m),
            Invoice("A-5", 200m, 20m, 30m));
        db.Bookings.AddRange(
            Booking("acc-1", new DateOnly(2025, 12, 31), 10m, "before the window"),
            Booking("acc-1", new DateOnly(2026, 1, 1), 2000m, "in window"),
            Booking("acc-1", new DateOnly(2026, 3, 31), 20m, "in window"),
            Booking("acc-1", new DateOnly(2026, 4, 1), 30m, "after the window"),
            Booking("acc-2", new DateOnly(2026, 1, 15), 5000m, "before the window"),
            Booking("acc-2", new DateOnly(2026, 2, 14), 40m, "in window"),
            Booking("acc-3", new DateOnly(2026, 2, 14), 50m, "no window"));
        await db.SaveChangesAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        await TestContainerLifecycle.Shared.DisposeAsync(_container);
    }

    /// <summary>Creates a fresh context bound to the container.</summary>
    public RulesDbContext CreateContext() {
        return new RulesDbContext(new DbContextOptionsBuilder<RulesDbContext>()
            .UseNpgsql(_connectionString)
            .Options);
    }

    private static RuleInvoice Invoice(string number, decimal gross, params decimal[] payments) {
        var invoice = new RuleInvoice { Id = Guid.CreateVersion7(), Number = number, Gross = gross };
        foreach (var amount in payments) {
            invoice.Payments.Add(new RulePayment { Id = Guid.CreateVersion7(), InvoiceId = invoice.Id, Amount = amount });
        }

        return invoice;
    }

    private static RuleBooking Booking(string account, DateOnly bookedOn, decimal amount, string note) {
        return new RuleBooking {
            Id = Guid.CreateVersion7(), Account = account, BookedOn = bookedOn, Amount = amount, Note = note,
        };
    }
}
