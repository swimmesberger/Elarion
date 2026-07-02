#pragma warning disable ASP0029 // The M.E.Validation extensibility surface is [Experimental]; Elarion.Validation is its deliberate consumer (ADR-0027).
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AwesomeAssertions;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Validation;
using Elarion.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Validation;
using Xunit;

namespace Elarion.Tests.Validation;

/// <summary>
/// Tests <see cref="MicrosoftRequestValidator"/> end-to-end through DI: <c>AddElarionValidation</c> +
/// <c>AddElarionValidationResolver</c> resolve the request's validation metadata, and error keys come back as
/// wire-named field paths under the canonical JSON naming policy.
/// </summary>
public sealed class MicrosoftRequestValidatorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configureServices = null) {
        var services = new ServiceCollection();
        services.AddElarionValidation();
        services.AddElarionValidationResolver(new TestResolver());
        configureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task InvalidRequest_ProducesCamelCaseFieldPathKeys() {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var request = new CreateClientRequest {
            Name = "ab",
            Priority = 0,
            Address = new AddressDto { Street = "far too long street" },
        };

        var result = await validator.ValidateAsync(typeof(CreateClientRequest), request, Ct);

        result.Should().NotBeNull();
        result!.FieldErrors.Keys.Should().BeEquivalentTo("name", "priority", "address.street");
        result!.FieldErrors["name"].Should().ContainSingle().Which.Should().Contain("Name");
    }

    [Fact]
    public async Task InvalidCollectionItem_PreservesIndexerSuffixInFieldPath() {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var request = new CreateClientRequest {
            Name = "valid",
            Priority = 1,
            Deliveries = [new AddressDto { Street = "ok" }, new AddressDto { Street = "far too long street" }],
        };

        var result = await validator.ValidateAsync(typeof(CreateClientRequest), request, Ct);

        result.Should().NotBeNull();
        result!.FieldErrors.Keys.Should().BeEquivalentTo("deliveries[1].street");
    }

    [Fact]
    public async Task ValidRequest_ReturnsNull() {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var request = new CreateClientRequest {
            Name = "valid",
            Priority = 1,
            Address = new AddressDto { Street = "ok" },
        };

        var result = await validator.ValidateAsync(typeof(CreateClientRequest), request, Ct);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UnannotatedType_ReturnsNull() {
        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();

        var result = await validator.ValidateAsync(typeof(UnannotatedRequest), new UnannotatedRequest(), Ct);

        result.Should().BeNull();
    }

    [Fact]
    public async Task NullNamingPolicy_LeavesFieldPathsUnchanged() {
        await using var provider = BuildProvider(services =>
            services.ConfigureElarionJson(o => o.PropertyNamingPolicy = null));
        using var scope = provider.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<IRequestValidator>();
        var request = new CreateClientRequest {
            Name = "ab",
            Priority = 1,
            Address = new AddressDto { Street = "far too long street" },
        };

        var result = await validator.ValidateAsync(typeof(CreateClientRequest), request, Ct);

        result.Should().NotBeNull();
        result!.FieldErrors.Keys.Should().BeEquivalentTo("Name", "Address.Street");
    }

    [Fact]
    public void AddElarionValidation_IsIdempotent() {
        var services = new ServiceCollection();
        services.AddElarionValidation();
        services.AddElarionValidation();

        services.Count(d => d.ServiceType == typeof(IRequestValidator)).Should().Be(1);
    }

    private sealed record CreateClientRequest {
        [StringLength(10, MinimumLength = 3)]
        public required string Name { get; init; }

        [Range(1, 100)]
        public int Priority { get; init; }

        public AddressDto? Address { get; init; }

        public List<AddressDto>? Deliveries { get; init; }
    }

    private sealed record AddressDto {
        [StringLength(5)]
        public required string Street { get; init; }
    }

    private sealed record UnannotatedRequest;

    /// <summary>
    /// A hand-written stand-in for the source-generated resolver of ADR-0027 item 4: constant-constructed
    /// attribute arrays behind the <see cref="IValidatableInfoResolver"/> seam.
    /// </summary>
    private sealed class TestResolver : IValidatableInfoResolver {
        public bool TryGetValidatableTypeInfo(Type type, [NotNullWhen(true)] out IValidatableInfo? validatableInfo) {
            if (type == typeof(CreateClientRequest)) {
                validatableInfo = new TestTypeInfo(typeof(CreateClientRequest), [
                    new TestPropertyInfo(typeof(CreateClientRequest), typeof(string), nameof(CreateClientRequest.Name),
                        [new StringLengthAttribute(10) { MinimumLength = 3 }]),
                    new TestPropertyInfo(typeof(CreateClientRequest), typeof(int), nameof(CreateClientRequest.Priority),
                        [new RangeAttribute(1, 100)]),
                    new TestPropertyInfo(typeof(CreateClientRequest), typeof(AddressDto), nameof(CreateClientRequest.Address), []),
                    new TestPropertyInfo(typeof(CreateClientRequest), typeof(List<AddressDto>), nameof(CreateClientRequest.Deliveries), []),
                ]);
                return true;
            }

            if (type == typeof(AddressDto)) {
                validatableInfo = new TestTypeInfo(typeof(AddressDto), [
                    new TestPropertyInfo(typeof(AddressDto), typeof(string), nameof(AddressDto.Street),
                        [new StringLengthAttribute(5)]),
                ]);
                return true;
            }

            validatableInfo = null;
            return false;
        }

        public bool TryGetValidatableParameterInfo(ParameterInfo parameterInfo, [NotNullWhen(true)] out IValidatableInfo? validatableInfo) {
            validatableInfo = null;
            return false;
        }
    }

    private sealed class TestTypeInfo(Type type, ValidatablePropertyInfo[] members) : ValidatableTypeInfo(type, members) {
        protected override ValidationAttribute[] GetValidationAttributes() => [];
    }

    private sealed class TestPropertyInfo(
        Type declaringType,
        Type propertyType,
        string name,
        ValidationAttribute[] attributes
    ) : ValidatablePropertyInfo(declaringType, propertyType, name, name) {
        protected override ValidationAttribute[] GetValidationAttributes() => attributes;
    }
}
