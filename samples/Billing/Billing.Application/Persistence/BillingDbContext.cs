using System.Text;
using Elarion.Actors.PostgreSql;
using Elarion.Auditing.EntityFrameworkCore;
using Elarion.EntityFrameworkCore;
using Elarion.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Billing.Application.Persistence;

/// <summary>The application's data-access context. The database is application logic, not an abstraction, so
/// handlers inject this concrete context directly (LINQ, raw SQL, provider functions) — there is no
/// `IAppDbContext` interface in front of it. It lives in the shared <c>Persistence</c> layer beside the
/// <c>[EntityConfiguration]</c> classes. <c>[GenerateDbSets]</c> fills this partial class with a
/// <c>DbSet&lt;T&gt;</c> per configured entity and a <c>ConfigureEntities</c> call; only provider
/// registration (<c>UseNpgsql</c> + connection string) is a host concern.</summary>
[GenerateDbSets]
public sealed partial class BillingDbContext(DbContextOptions<BillingDbContext> options)
    : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        ConfigureEntities(modelBuilder);   // generated — applies every discovered IEntityTypeConfiguration<T>

        // PostgreSQL convention: snake_case the app's own tables/columns/keys/indexes. Applied here — after the
        // app entities are configured but *before* the framework tables below — so it only touches this app's
        // entities; the framework tables (outbox, audit log) set their own explicit snake_case names.
        ApplySnakeCaseNames(modelBuilder);

        modelBuilder.UseElarionOutbox();          // integration-event outbox table (Elarion.Messaging.Outbox)
        modelBuilder.UseElarionAuditing();        // framework audit-log table (Elarion.Auditing.EntityFrameworkCore, ADR-0045)
        modelBuilder.UseElarionActorSnapshots();  // actor state snapshot table (Elarion.Actors.PostgreSql, ADR-0047)
    }

    private static void ApplySnakeCaseNames(ModelBuilder modelBuilder) {
        foreach (var entity in modelBuilder.Model.GetEntityTypes()) {
            if (entity.GetTableName() is { } table) {
                entity.SetTableName(ToSnakeCase(table));
            }

            foreach (var property in entity.GetProperties()) {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys()) {
                key.SetName(ToSnakeCase(key.GetName()!));
            }

            foreach (var index in entity.GetIndexes()) {
                index.SetDatabaseName(ToSnakeCase(index.GetDatabaseName()!));
            }
        }
    }

    private static string ToSnakeCase(string name) {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++) {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && name[i - 1] != '_' && !char.IsUpper(name[i - 1])) {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
