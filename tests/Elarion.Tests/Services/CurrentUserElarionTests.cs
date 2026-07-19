using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class CurrentUserFrameworkTests {
    [Fact]
    public void FrameworkAssembly_DoesNotReferenceAspNetCore() {
        var referencedAssemblies = typeof(ICurrentUser)
            .Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        referencedAssemblies.Should()
            .NotContain(name => name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
