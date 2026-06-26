using System.Security.Claims;
using AwesomeAssertions;
using Elarion.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.AspNetCore;

public sealed class CurrentUserSnapshotClaimsTests {
    [Fact]
    public async Task ExposesClaimsFromTheInitializedPrincipal() {
        var snapshot = new CurrentUserSnapshot(Options.Create(new AspNetCoreCurrentUserOptions()));
        var identity = new ClaimsIdentity(
            [
                new Claim("permission", "tenants.read"),
                new Claim("permission", "tenants.write"),
                new Claim("scope", "files"),
            ],
            authenticationType: "test");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        // The middleware is the real path that snapshots the principal into the scoped current user.
        await new CurrentUserMiddleware(_ => Task.CompletedTask).InvokeAsync(httpContext, snapshot);

        snapshot.HasClaim("permission", "tenants.read").Should().BeTrue();
        snapshot.HasClaim("permission", "absent").Should().BeFalse();
        snapshot.GetClaimValues("permission").Should().BeEquivalentTo("tenants.read", "tenants.write");
        snapshot.GetClaimValues("missing").Should().BeEmpty();
    }
}
