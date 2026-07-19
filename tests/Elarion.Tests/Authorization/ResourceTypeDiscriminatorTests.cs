using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Xunit;

namespace Elarion.Tests.Authorization;

/// <summary>
/// Regression coverage for the resource-type discriminator (finding H10): two entities that share a simple
/// <c>Type.Name</c> in different namespaces must NOT collapse to the same grant discriminator, or a grant on one
/// would silently authorize the other. The default discriminator is the namespace-qualified full name.
/// </summary>
public sealed class ResourceTypeDiscriminatorTests {
    [Fact]
    public void For_UsesFullName_SoSameNamedTypesDoNotCollide() {
        var crm = ResourceTypeDiscriminator.For(typeof(Crm.Contact));
        var billing = ResourceTypeDiscriminator.For(typeof(Billing.Contact));

        crm.Should().Be(typeof(Crm.Contact).FullName).And.EndWith("Crm+Contact");
        billing.Should().Be(typeof(Billing.Contact).FullName).And.EndWith("Billing+Contact");
        crm.Should().NotBe(billing);
    }

    [Fact]
    public void Resolve_PrefersExplicitOverride() {
        ResourceTypeDiscriminator.Resolve(typeof(Crm.Contact), "Contact").Should().Be("Contact");
    }

    [Fact]
    public void Resolve_FallsBackToFullNameWhenOverrideIsNullOrEmpty() {
        var expected = typeof(Crm.Contact).FullName;
        ResourceTypeDiscriminator.Resolve(typeof(Crm.Contact), null).Should().Be(expected);
        ResourceTypeDiscriminator.Resolve(typeof(Crm.Contact), "").Should().Be(expected);
    }

    [Fact]
    public void RequirementBinding_ResolvesDiscriminatorFromTypeByDefault() {
        var binding = new ResourceRequirementBinding<object>(
            typeof(Crm.Contact), ResourceOperation.Read, static _ => null);

        binding.ResourceTypeName.Should().Be(typeof(Crm.Contact).FullName);
    }

    [Fact]
    public void RequirementBinding_HonoursExplicitDiscriminator() {
        var binding = new ResourceRequirementBinding<object>(
            typeof(Crm.Contact), ResourceOperation.Read, static _ => null, "Contact");

        binding.ResourceTypeName.Should().Be("Contact");
    }

    private static class Crm {
        public sealed class Contact;
    }

    private static class Billing {
        public sealed class Contact;
    }
}
