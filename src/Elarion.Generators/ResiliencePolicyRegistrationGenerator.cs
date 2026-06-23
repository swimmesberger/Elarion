using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates implementation-neutral resilience policy metadata from framework attributes.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ResiliencePolicyRegistrationGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.Abstractions.GenerateResiliencePoliciesAttribute";

    private const string ResiliencePolicyAttributeMetadataName =
        "Elarion.Abstractions.Resilience.ResiliencePolicyAttribute";

    private static readonly DiagnosticDescriptor InvalidPolicyDescriptor = new(
        id: "ELRES001",
        title: "Invalid resilience policy",
        messageFormat: "Resilience policy '{0}' is invalid: {1}",
        category: "Elarion.Abstractions.Resilience",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicatePolicyNameDescriptor = new(
        id: "ELRES002",
        title: "Duplicate resilience policy name",
        messageFormat: "Resilience policy name '{0}' is used more than once",
        category: "Elarion.Abstractions.Resilience",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private sealed record PolicyInfo(
        string Namespace,
        string TypeName,
        string Name,
        RetryInfo? Retry,
        TimeoutInfo? Timeout,
        LocationInfo Location,
        EquatableArray<DiagnosticInfo> Diagnostics);

    private sealed record RetryInfo(
        int MaxRetryAttempts,
        long DelayTicks,
        int Backoff,
        long? MaxDelayTicks,
        bool UseJitter);

    private sealed record TimeoutInfo(long TimeoutTicks);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Discover policies via the attribute index: the transform only re-runs for changed
        // [ResiliencePolicy] declarations, not on every keystroke.
        var policies = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ResiliencePolicyAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol classSymbol && ctx.Attributes.Length > 0
                    ? CreatePolicyInfo(classSymbol, ctx.Attributes[0])
                    : null)
            .Where(static policy => policy is not null)
            .Select(static (policy, _) => policy!)
            .Collect()
            .WithTrackingName(TrackingNames.Policies);

        // Note 43: This generator is opt-in, so projects do not pay generation cost unless they request resilience policies.
        // The trigger and assembly name project off the compilation to small equatable values, so they stay
        // "unchanged" across edits that do not touch the opt-in or the assembly name.
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);
        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName);

        var combined = policies.Combine(trigger).Combine(assemblyName).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((policyList, hasTrigger), assemblyName) = source;
            if (!hasTrigger)
            {
                return;
            }

            ReportDiagnostics(policyList, spc);
            ReportDuplicateNames(policyList, spc);

            // Note 44: Invalid policies still report diagnostics, but no invalid registration code is emitted.
            var validPolicies = policyList
                .Where(static policy => policy.Diagnostics.IsEmpty)
                .ToArray();
            if (validPolicies.Length == 0)
            {
                return;
            }

            // Note 45: Roslyn symbols provide semantic information, avoiding fragile text parsing of attribute syntax.
            var generated = GenerateSource(assemblyName ?? "Generated", validPolicies);
            spc.AddSource("ResiliencePolicyRegistration.g.cs", SourceText.From(generated, Encoding.UTF8));
        });
    }

    private static class TrackingNames
    {
        public const string Policies = "ResiliencePolicies";
        public const string Combined = "ResiliencePoliciesCombined";
    }

    private static PolicyInfo CreatePolicyInfo(INamedTypeSymbol classSymbol, AttributeData attribute)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var name = GetRequiredName(attribute);
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(CreateDiagnostic(
                classSymbol,
                "Policy name must be a non-empty string."));
        }

        var retry = ParseRetry(classSymbol, attribute, diagnostics);
        var timeout = ParseTimeout(classSymbol, attribute, diagnostics);
        if (retry is null && timeout is null)
        {
            // Note 46: Empty policy declarations are rejected because a named policy with no behavior is likely a mistake.
            diagnostics.Add(CreateDiagnostic(
                classSymbol,
                "At least one retry or timeout option must be configured."));
        }

        return new PolicyInfo(
            classSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            classSymbol.Name,
            name,
            retry,
            timeout,
            LocationInfo.From(classSymbol),
            diagnostics.ToImmutable());
    }

    private static RetryInfo? ParseRetry(
        INamedTypeSymbol classSymbol,
        AttributeData attribute,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (!HasAnyNamedArgument(attribute, "MaxRetryAttempts", "Delay", "Backoff", "MaxDelay", "UseJitter"))
        {
            return null;
        }

        var maxRetryAttempts = GetIntNamedArgument(attribute, "MaxRetryAttempts", 3);
        if (maxRetryAttempts < 0)
        {
            diagnostics.Add(CreateDiagnostic(
                classSymbol,
                "MaxRetryAttempts must be greater than or equal to zero."));
        }

        var delayText = GetStringNamedArgument(attribute, "Delay") ?? "2s";
        // Note 47: Durations are parsed by the generator so invalid literals fail at compile time instead of at runtime.
        var delay = ParseDuration(classSymbol, diagnostics, delayText, "Delay");
        var backoff = GetEnumNamedArgument(attribute, "Backoff", 0);
        var maxDelayText = GetStringNamedArgument(attribute, "MaxDelay");
        var maxDelay = maxDelayText is null
            ? (TimeSpan?)null
            : ParseDuration(classSymbol, diagnostics, maxDelayText, "MaxDelay");
        var useJitter = GetBoolNamedArgument(attribute, "UseJitter", false);

        return new RetryInfo(
            maxRetryAttempts,
            delay.GetValueOrDefault().Ticks,
            backoff,
            maxDelay?.Ticks,
            useJitter);
    }

    private static TimeoutInfo? ParseTimeout(
        INamedTypeSymbol classSymbol,
        AttributeData attribute,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var timeoutText = GetStringNamedArgument(attribute, "Timeout");
        if (timeoutText is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(timeoutText))
        {
            diagnostics.Add(CreateDiagnostic(
                classSymbol,
                "Timeout must be a non-empty duration."));
            return new TimeoutInfo(0);
        }

        var timeout = ParseDuration(classSymbol, diagnostics, timeoutText, "Timeout");
        return new TimeoutInfo(timeout.GetValueOrDefault().Ticks);
    }

    private static TimeSpan? ParseDuration(
        INamedTypeSymbol classSymbol,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        string value,
        string propertyName)
    {
        try
        {
            var parsed = ParseDuration(value);
            if (parsed <= TimeSpan.Zero)
            {
                diagnostics.Add(CreateDiagnostic(
                    classSymbol,
                    $"{propertyName} must be greater than zero."));
            }

            return parsed;
        }
        catch (FormatException ex)
        {
            diagnostics.Add(CreateDiagnostic(classSymbol, ex.Message));
            return null;
        }
    }

    private static TimeSpan ParseDuration(string value)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        string numberText;
        Func<double, TimeSpan>? factory;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            numberText = value.Substring(0, value.Length - 2);
            factory = TimeSpan.FromMilliseconds;
        }
        else
        {
            numberText = value.Length > 0 ? value.Substring(0, value.Length - 1) : value;
            factory = value.Length > 0
                ? value[value.Length - 1] switch
                {
                    'd' or 'D' => TimeSpan.FromDays,
                    'h' or 'H' => TimeSpan.FromHours,
                    'm' or 'M' => TimeSpan.FromMinutes,
                    's' or 'S' => TimeSpan.FromSeconds,
                    _ => null
                }
                : null;
        }

        if (factory is null ||
            numberText.Length == 0 ||
            !double.TryParse(numberText, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
        {
            throw new FormatException(
                $"Duration '{value}' is invalid. Use invariant TimeSpan text or a number with the suffix ms, s, m, h, or d.");
        }

        return factory(amount);
    }

    private static void ReportDiagnostics(
        IReadOnlyList<PolicyInfo> policies,
        SourceProductionContext spc)
    {
        foreach (var policy in policies)
        {
            foreach (var diagnostic in policy.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            }
        }
    }

    private static void ReportDuplicateNames(
        IReadOnlyList<PolicyInfo> policies,
        SourceProductionContext spc)
    {
        foreach (var group in policies
                     .Where(static policy => policy.Diagnostics.IsEmpty)
                     .GroupBy(static policy => policy.Name, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var policy in group)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DuplicatePolicyNameDescriptor,
                    policy.Location.ToLocation(),
                    group.Key));
            }
        }
    }

    private static string GenerateSource(string assemblyName, IReadOnlyList<PolicyInfo> policies)
    {
        var assemblyIdentifier = SanitizeIdentifier(assemblyName);
        var registrationType = $"{assemblyIdentifier}ResiliencePolicyRegistration";
        var registrationMethod = $"Add{assemblyIdentifier}ResiliencePolicies";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ResiliencePolicyRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();

        foreach (var policy in policies.OrderBy(static policy => policy.Name, StringComparer.Ordinal))
        {
            AppendPolicyType(sb, policy);
            sb.AppendLine();
        }

        sb.AppendLine($"public static class {registrationType}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {registrationMethod}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");
        foreach (var policy in policies.OrderBy(static policy => policy.Name, StringComparer.Ordinal))
        {
            var prefix = string.IsNullOrEmpty(policy.Namespace) ? string.Empty : $"global::{policy.Namespace}.";
            sb.AppendLine($"        {prefix}{policy.TypeName}.Add{policy.TypeName}(services);");
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendPolicyType(StringBuilder sb, PolicyInfo policy)
    {
        if (!string.IsNullOrEmpty(policy.Namespace))
        {
            sb.AppendLine($"namespace {policy.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine();
        }

        sb.AppendLine($"public static partial class {policy.TypeName}");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string Name = \"{EscapeString(policy.Name)}\";");
        sb.AppendLine();
        sb.AppendLine("    public static global::Elarion.Abstractions.Resilience.ResiliencePolicyReference Reference { get; } = new() { Name = Name };");
        sb.AppendLine();
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection Add{policy.TypeName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");
        // Note 48: Generated policy registration is metadata-only so applications can plug in any resilience runtime.
        AppendMetadataRegistration(sb, policy);
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        if (!string.IsNullOrEmpty(policy.Namespace))
        {
            sb.AppendLine();
            sb.AppendLine("}");
        }
    }

    private static void AppendMetadataRegistration(StringBuilder sb, PolicyInfo policy)
    {
            sb.AppendLine("        services.AddSingleton(new global::Elarion.Abstractions.Resilience.ResiliencePolicyMetadataRegistration");
            sb.AppendLine("        {");
            sb.AppendLine("            Metadata = new global::Elarion.Abstractions.Resilience.ResiliencePolicyMetadata");
            sb.AppendLine("            {");
            sb.AppendLine("                Name = Name,");
            if (policy.Retry is { } retry)
            {
                sb.AppendLine("                Retry = new global::Elarion.Abstractions.Resilience.ResilienceRetryOptions");
                sb.AppendLine("                {");
                sb.AppendLine($"                    MaxRetryAttempts = {retry.MaxRetryAttempts},");
                sb.AppendLine($"                    Delay = global::System.TimeSpan.FromTicks({retry.DelayTicks.ToString(CultureInfo.InvariantCulture)}L),");
                sb.AppendLine($"                    Backoff = global::Elarion.Abstractions.Resilience.ResilienceBackoffType.{FormatFrameworkBackoff(retry.Backoff)},");
                if (retry.MaxDelayTicks is { } maxDelayTicks)
                {
                    sb.AppendLine($"                    MaxDelay = global::System.TimeSpan.FromTicks({maxDelayTicks.ToString(CultureInfo.InvariantCulture)}L),");
                }

                sb.AppendLine($"                    UseJitter = {ToCSharpBool(retry.UseJitter)}");
                sb.AppendLine("                },");
            }
            else
            {
                sb.AppendLine("                Retry = null,");
            }

            if (policy.Timeout is { } timeout)
            {
                sb.AppendLine($"                Timeout = global::System.TimeSpan.FromTicks({timeout.TimeoutTicks.ToString(CultureInfo.InvariantCulture)}L)");
            }
            else
            {
                sb.AppendLine("                Timeout = null");
            }

            sb.AppendLine("            }");
            sb.AppendLine("        });");
    }

    private static DiagnosticInfo CreateDiagnostic(INamedTypeSymbol classSymbol, string reason) =>
        DiagnosticInfo.Create(
            InvalidPolicyDescriptor,
            LocationInfo.From(classSymbol),
            classSymbol.Name,
            reason);

    private static string GetRequiredName(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 &&
        attribute.ConstructorArguments[0].Value is string value
            ? value
            : string.Empty;

    private static string? GetStringNamedArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is string value)
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasAnyNamedArgument(AttributeData attribute, params string[] names)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            foreach (var name in names)
            {
                if (argument.Key == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int GetIntNamedArgument(AttributeData attribute, string name, int defaultValue)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is int value)
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static int GetEnumNamedArgument(AttributeData attribute, string name, int defaultValue)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is int value)
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static bool GetBoolNamedArgument(AttributeData attribute, string name, bool defaultValue)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is bool value)
            {
                return value;
            }
        }

        return defaultValue;
    }

    private static string FormatBackoff(int backoff) =>
        backoff switch
        {
            1 => "Linear",
            2 => "Exponential",
            _ => "Constant"
        };

    private static string FormatFrameworkBackoff(int backoff) => FormatBackoff(backoff);

    private static string SanitizeIdentifier(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        if (sb.Length == 0 || !char.IsLetter(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private static string EscapeString(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

    private static string ToCSharpBool(bool value) => value ? "true" : "false";
}
