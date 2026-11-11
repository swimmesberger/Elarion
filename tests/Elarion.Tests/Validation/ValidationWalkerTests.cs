#if NET11_0_OR_GREATER
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Elarion.Abstractions.Validation;
using Elarion.Validation;
using Elarion.Validation.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Validation;
using Xunit;

namespace Elarion.Tests.Validation;

/// <summary>
/// The net11 walker Elarion.Validation carries for Microsoft.Extensions.Validation 11 (whose own walker base classes
/// are no longer public): asynchronous attributes and <see cref="IAsyncValidatableObject"/> run through
/// <see cref="MicrosoftRequestValidator"/> with the same ordering rules as Microsoft's template — synchronous
/// attributes first, asynchronous ones only when those passed, and object-level validation only when every member
/// passed.
/// </summary>
public sealed class ValidationWalkerTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AsyncAttribute_ReportsWireNamedError() {
        var probe = new AsyncProbe();
        var result = await ValidateAsync(new SignupRequest { Handle = "taken", Profile = ValidProfile }, probe);

        result.Should().NotBeNull();
        result!.FieldErrors.Keys.Should().BeEquivalentTo("handle");
        result.FieldErrors["handle"].Should().ContainSingle().Which.Should().Be("Handle is taken.");
        probe.AttributeCalls.Should().Be(1);
    }

    [Fact]
    public async Task AsyncAttribute_IsSkippedWhenASynchronousAttributeFailed() {
        var probe = new AsyncProbe();
        var result = await ValidateAsync(new SignupRequest { Handle = "taken-but-far-too-long", Profile = ValidProfile },
            probe);

        result!.FieldErrors["handle"].Should().ContainSingle().Which.Should().Contain("maximum length");
        probe.AttributeCalls.Should().Be(0);
    }

    [Fact]
    public async Task AsyncValidatableObject_RunsOnlyWhenTheObjectsOwnMembersPassed() {
        var probe = new AsyncProbe();

        var objectFailure = await ValidateAsync(new SignupRequest { Handle = "free", Profile = new Profile() }, probe);
        objectFailure!.FieldErrors.Keys.Should().BeEquivalentTo("profile.displayName");

        var memberFailure = await ValidateAsync(
            new SignupRequest { Handle = "free", Profile = new Profile { Nickname = "far too long" } }, probe);
        memberFailure!.FieldErrors.Keys.Should().BeEquivalentTo("profile.nickname");
        probe.ObjectCalls.Should().Be(1);
    }

    [Fact]
    public async Task ValidRequest_ReturnsNull() {
        var result = await ValidateAsync(
            new SignupRequest { Handle = "free", Profile = ValidProfile }, new AsyncProbe());

        result.Should().BeNull();
    }

    private static Profile ValidProfile => new() { DisplayName = "Free" };

    private static async Task<RequestValidationErrors?> ValidateAsync(SignupRequest request, AsyncProbe probe) {
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddElarionValidation();
        services.AddElarionValidationResolver(new SignupResolver());
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IRequestValidator>()
            .ValidateAsync(typeof(SignupRequest), request, Ct);
    }

    private sealed class AsyncProbe {
        public int AttributeCalls;
        public int ObjectCalls;
    }

    private sealed record SignupRequest {
        public required string Handle { get; init; }
        public required Profile Profile { get; init; }
    }

    private sealed record Profile : IAsyncValidatableObject {
        public string? DisplayName { get; init; }

        public string? Nickname { get; init; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) {
            throw new InvalidOperationException("Validate this object with ValidateAsync.");
        }

        public async IAsyncEnumerable<ValidationResult> ValidateAsync(ValidationContext validationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken) {
            Interlocked.Increment(ref validationContext.GetRequiredService<AsyncProbe>().ObjectCalls);
            await Task.Yield();
            if (DisplayName is null)
                yield return new ValidationResult("A display name is required.", [nameof(DisplayName)]);
        }
    }

    private sealed class HandleAvailableAttribute : AsyncValidationAttribute {
        protected override ValidationResult IsValid(object? value, ValidationContext validationContext) {
            throw new InvalidOperationException("Validate this attribute with IsValidAsync.");
        }

        protected override async Task<ValidationResult?> IsValidAsync(object? value,
            ValidationContext validationContext, CancellationToken cancellationToken) {
            Interlocked.Increment(ref validationContext.GetRequiredService<AsyncProbe>().AttributeCalls);
            await Task.Yield();
            return value is "taken" ? new ValidationResult("Handle is taken.") : ValidationResult.Success;
        }
    }

    /// <summary>A hand-written stand-in for the generated resolver: the net11 info subclasses it emits.</summary>
    private sealed class SignupResolver : IValidatableInfoResolver {
        private static readonly IValidatableTypeInfo Signup = new TestTypeInfo(typeof(SignupRequest), [
            new TestPropertyInfo(typeof(SignupRequest), typeof(string), nameof(SignupRequest.Handle),
                [new StringLengthAttribute(10), new HandleAvailableAttribute()]),
            new TestPropertyInfo(typeof(SignupRequest), typeof(Profile), nameof(SignupRequest.Profile), [])
        ]);

        private static readonly IValidatableTypeInfo ProfileInfo = new TestTypeInfo(typeof(Profile), [
            new TestPropertyInfo(typeof(Profile), typeof(string), nameof(Profile.Nickname), [new StringLengthAttribute(5)])
        ]);

        public bool TryGetValidatableTypeInfo(Type type, [NotNullWhen(true)] out IValidatableTypeInfo? validatableInfo) {
            validatableInfo = type == typeof(SignupRequest) ? Signup : type == typeof(Profile) ? ProfileInfo : null;
            return validatableInfo is not null;
        }

        public bool TryGetValidatableParameterInfo(ParameterInfo parameterInfo,
            [NotNullWhen(true)] out IValidatableParameterInfo? validatableInfo) {
            validatableInfo = null;
            return false;
        }
    }

    private sealed class TestTypeInfo(Type type, ValidatablePropertyInfo[] members) : ValidatableTypeInfo(type, members) {
        protected override ValidationAttribute[] GetValidationAttributes() {
            return [];
        }
    }

    private sealed class TestPropertyInfo(Type declaringType, Type propertyType, string name, ValidationAttribute[] attributes)
        : ValidatablePropertyInfo(declaringType, propertyType, name, name) {
        protected override ValidationAttribute[] GetValidationAttributes() {
            return attributes;
        }
    }
}
#endif
