using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Authorization.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// Verifies that <see cref="EfCoreResourceGrantStore{TDbContext}"/> writes enlist in the caller's ambient EF Core
/// transaction: a grant or revoke issued while the context holds an open transaction commits or rolls back
/// together with it. Reuses the contacts + grants schema fixture. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ResourceGrantStoreTransactionTests(ResourceGrantSharingFixture fixture)
    : IClassFixture<ResourceGrantSharingFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildProvider(ResourceGrantSharingFixture fixture) {
        var services = new ServiceCollection();
        services.AddDbContext<ContactsDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionResourceAuthorization<ContactsDbContext>();
        return services.BuildServiceProvider();
    }

    private static ResourceGrant ReadGrant(string resourceId) =>
        new("Contact", resourceId, ResourcePrincipal.Role("Hausmeister"), ResourceOperation.Read);

    [Fact]
    public async Task GrantAsync_RolledBackWithCallerTransaction_PersistsNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var resourceId = Guid.NewGuid().ToString();

        await using (var provider = BuildProvider(fixture))
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<ContactsDbContext>();
            var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await grantStore.GrantAsync(ReadGrant(resourceId), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyProvider = BuildProvider(fixture);
        await using var verifyScope = verifyProvider.CreateAsyncScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
        (await verifyStore.GetGrantsAsync("Contact", resourceId, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task GrantAsync_DuplicateIsIdempotent_NoSecondRow() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var resourceId = Guid.NewGuid().ToString();

        await using var provider = BuildProvider(fixture);
        await using var scope = provider.CreateAsyncScope();
        var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();

        await grantStore.GrantAsync(ReadGrant(resourceId), Ct);
        await grantStore.GrantAsync(ReadGrant(resourceId), Ct);

        (await grantStore.GetGrantsAsync("Contact", resourceId, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task GrantAsync_DuplicateInAmbientTransaction_DoesNotPoisonTransaction() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var resourceId = Guid.NewGuid().ToString();

        await using var provider = BuildProvider(fixture);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ContactsDbContext>();
        var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();

        // The grant already exists.
        await grantStore.GrantAsync(ReadGrant(resourceId), Ct);

        // A duplicate grant issued inside the caller's ambient transaction (alongside a business write) must be a
        // no-op via ON CONFLICT DO NOTHING — never a 23505 unique violation that would abort/poison the whole
        // transaction and fail the later commit of the business row.
        var contact = new Contact {
            Id = Guid.NewGuid(), OwnerId = Guid.NewGuid(), Name = "Committed alongside a duplicate grant",
        };
        await using (var transaction = await db.Database.BeginTransactionAsync(Ct)) {
            db.Contacts.Add(contact);
            await db.SaveChangesAsync(Ct);
            await grantStore.GrantAsync(ReadGrant(resourceId), Ct);
            await transaction.CommitAsync(Ct);
        }

        (await db.Contacts.AsNoTracking().AnyAsync(c => c.Id == contact.Id, Ct)).Should().BeTrue();
        (await grantStore.GetGrantsAsync("Contact", resourceId, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task RevokeAsync_RolledBackWithCallerTransaction_KeepsGrant() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var resourceId = Guid.NewGuid().ToString();

        await using (var seedProvider = BuildProvider(fixture))
        await using (var seedScope = seedProvider.CreateAsyncScope()) {
            await seedScope.ServiceProvider.GetRequiredService<IResourceGrantStore>().GrantAsync(ReadGrant(resourceId), Ct);
        }

        await using (var provider = BuildProvider(fixture))
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<ContactsDbContext>();
            var grantStore = scope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await grantStore.RevokeAsync(ReadGrant(resourceId), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyProvider = BuildProvider(fixture);
        await using var verifyScope = verifyProvider.CreateAsyncScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IResourceGrantStore>();
        (await verifyStore.GetGrantsAsync("Contact", resourceId, Ct)).Should().ContainSingle();
    }
}
