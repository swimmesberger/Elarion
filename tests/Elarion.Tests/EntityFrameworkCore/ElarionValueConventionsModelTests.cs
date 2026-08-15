using AwesomeAssertions;
using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Elarion.Tests.EntityFrameworkCore;

/// <summary>
/// Model-level cover for the opt-in value-shape conventions: what the built model actually claims, against a
/// real provider's model services (SQLite — no connection is opened, but the conventions and type-mapping
/// source are the real ones, unlike the banned InMemory provider).
/// </summary>
public sealed class ElarionValueConventionsModelTests {
    [Fact]
    public void EnumStringConversions_ApplyToEnumProperties_IncludingNullable() {
        using var context = CreateContext();

        ProviderClrType(context, nameof(ConventionWidget.Status)).Should().Be(typeof(string));
        ProviderClrType(context, nameof(ConventionWidget.OptionalStatus)).Should().Be(typeof(string));
    }

    [Fact]
    public void EnumStringConversions_LeaveExplicitConfigurationAlone() {
        using var context = CreateContext();

        // HasConversion<int>() — an explicit provider type.
        ProviderClrType(context, nameof(ConventionWidget.OrdinalStatus)).Should().Be(typeof(int));

        // HasConversion(to, from) — an explicit converter instance storing a one-character code.
        ProviderClrType(context, nameof(ConventionWidget.CodedStatus)).Should().Be(typeof(string));
        var coded = Property(context, nameof(ConventionWidget.CodedStatus)).GetTypeMapping().Converter!;
        coded.ConvertToProvider(ConventionStatus.Retired).Should().Be("R");
    }

    [Fact]
    public void EnumStringConversions_LeaveNonEnumPropertiesAlone() {
        using var context = CreateContext();

        Property(context, nameof(ConventionWidget.Name)).GetTypeMapping().Converter.Should().BeNull();
        Property(context, nameof(ConventionWidget.Count)).GetTypeMapping().Converter.Should().BeNull();
    }

    [Fact]
    public void JsonStringArray_AttachesConverterAndSequenceComparer() {
        using var context = CreateContext();
        var property = Property(context, nameof(ConventionWidget.Tags));

        var converter = property.GetTypeMapping().Converter;
        converter.Should().NotBeNull();
        converter!.ConvertToProvider(new[] { "a", "b" }).Should().Be("[\"a\",\"b\"]");
        converter.ConvertFromProvider("[\"a\",\"b\"]").Should().BeEquivalentTo(new[] { "a", "b" });

        // The comparer is the other half of the pair — without it a converted collection is compared by
        // reference and every in-place edit is invisible to change detection.
        var comparer = property.GetValueComparer();
        comparer.Should().NotBeNull();
        comparer.Equals(new[] { "a", "b" }, new[] { "a", "b" }).Should().BeTrue();
        comparer.Equals(new[] { "a", "b" }, new[] { "b", "a" }).Should().BeFalse();
    }

    [Fact]
    public void JsonStringArray_ReadsAbsentColumnAsEmpty() {
        using var context = CreateContext();
        var converter = Property(context, nameof(ConventionWidget.Tags)).GetTypeMapping().Converter!;

        // A column added by a migration without a backfill must be readable, not a startup exception.
        converter.ConvertFromProvider("").Should().BeEquivalentTo(Array.Empty<string>());
        converter.ConvertToProvider(Array.Empty<string>()).Should().Be("[]");
    }

    private static Type? ProviderClrType(DbContext context, string propertyName) {
        return Property(context, propertyName).GetTypeMapping().Converter?.ProviderClrType;
    }

    private static IProperty Property(DbContext context, string propertyName) {
        var entityType = context.Model.FindEntityType(typeof(ConventionWidget));
        entityType.Should().NotBeNull();
        var property = entityType!.FindProperty(propertyName);
        property.Should().NotBeNull($"'{propertyName}' should be mapped");
        return property!;
    }

    private static ConventionsDbContext CreateContext() {
        // Model building needs provider services, not a live connection.
        return new ConventionsDbContext(new DbContextOptionsBuilder<ConventionsDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);
    }
}

internal enum ConventionStatus {
    Draft,
    Active,
    Retired
}

internal sealed class ConventionWidget {
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public ConventionStatus Status { get; set; }
    public ConventionStatus? OptionalStatus { get; set; }
    public ConventionStatus OrdinalStatus { get; set; }
    public ConventionStatus CodedStatus { get; set; }
    public string[] Tags { get; set; } = [];
}

internal sealed class ConventionsDbContext(DbContextOptions<ConventionsDbContext> options) : DbContext(options) {
    public DbSet<ConventionWidget> Widgets => Set<ConventionWidget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<ConventionWidget>(builder => {
            builder.HasKey(widget => widget.Id);
            builder.Property(widget => widget.OrdinalStatus).HasConversion<int>();
            builder.Property(widget => widget.CodedStatus).HasConversion(
                status => status.ToString().Substring(0, 1),
                code => code == "R" ? ConventionStatus.Retired :
                    code == "A" ? ConventionStatus.Active : ConventionStatus.Draft);
            builder.Property(widget => widget.Tags).HasElarionJsonStringArray();
        });

        // The post-pass runs last, after the entity configurations, exactly as a host would call it.
        modelBuilder.UseElarionEnumStringConversions();
    }
}
