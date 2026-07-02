using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Elarion.Generators;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class SchedulerRegistrationGeneratorTests
{
    [Fact]
    public void GenerateScheduledJobs_AttributedMethod_EmitsTypedDescriptor()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob(
                        "billing.daily",
                        FixedRate = "1d",
                        Group = "billing",
                        MaxConcurrentRuns = 2,
                        MisfirePolicy = Elarion.Abstractions.Scheduling.ScheduledJobMisfirePolicy.CatchUp,
                        Enabled = "${Modules:Billing:Enabled}")]
                    public System.Threading.Tasks.ValueTask RunAsync(
                        Elarion.Abstractions.Scheduling.IScheduledJobContext context,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("AddSampleScheduledJobs");
        generatedSource.Should().Contain("services.TryAddScoped<global::Sample.Jobs.BillingJobs>();");
        generatedSource.Should().Contain("Name = \"billing.daily\"");
        generatedSource.Should().Contain("ScheduledJobSchedule.FixedRate(\"1d\", null, true)");
        generatedSource.Should().Contain("Group = \"billing\"");
        generatedSource.Should().Contain("MisfirePolicy = global::Elarion.Abstractions.Scheduling.ScheduledJobMisfirePolicy.CatchUp");
        generatedSource.Should().Contain("MaxConcurrentRuns = 2");
        generatedSource.Should().Contain("Enabled = \"${Modules:Billing:Enabled}\"");
        generatedSource.Should().Contain("Placement = global::Elarion.Abstractions.Scheduling.JobPlacement.Cluster");
        generatedSource.Should().Contain("SchedulerRegistrationGeneratorTestsScheduledJobRegistrationJobReferences");
        generatedSource.Should().Contain("BillingDaily { get; } = new() { Name = \"billing.daily\" };");
        generatedSource.Should().Contain("GetRequiredService<global::Sample.Jobs.BillingJobs>()");
        generatedSource.Should().Contain("job.RunAsync(context, ct)");
        generatedSource.Should().NotContain("System.Reflection");
        generatedSource.Should().NotContain("GetCustomAttributes");
        generatedSource.Should().NotContain("InMemoryScheduler");
        generatedSource.Should().NotContain("AddElarionScheduler");
        generatedSource.Should().NotContain("BackgroundService");
        generatedSource.Should().NotContain("IHostedService");
    }

    [Fact]
    public void GenerateScheduledJobs_EveryNodePlacement_EmitsPlacement()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class CacheJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob(
                        "cache.refresh",
                        FixedRate = "5m",
                        Placement = Elarion.Abstractions.Scheduling.JobPlacement.EveryNode)]
                    public System.Threading.Tasks.ValueTask RunAsync(
                        Elarion.Abstractions.Scheduling.IScheduledJobContext context,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("Name = \"cache.refresh\"");
        generatedSource.Should().Contain("Placement = global::Elarion.Abstractions.Scheduling.JobPlacement.EveryNode");
    }

    [Fact]
    public void GenerateScheduledJobs_ModuleScoped_EmitsPerModuleMethodAndDefaultServicesFiller()
    {
        var source = CreateSource(
            """
            namespace Elarion.Abstractions.Modules {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class AppModuleAttribute : System.Attribute {
                    public AppModuleAttribute(string name) { Name = name; }
                    public string Name { get; }
                }
            }

            namespace Sample.Jobs {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        var perModule = GetGeneratedSource(result, "BillingScheduledJobExtensions.g.cs");
        perModule.Should().Contain(
            "public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddBillingScheduledJobs(");
        perModule.Should().Contain("Name = \"billing.daily\"");
        // Per-module methods do not carry the assembly-level job references type.
        perModule.Should().NotContain("JobReferences");

        var anyTree = string.Concat(result.GeneratedTrees.Select(tree => tree.GetText().ToString()));
        anyTree.Should().Contain(
            "global::Sample.Jobs.BillingScheduledJobExtensions.AddBillingScheduledJobs(services);");
        anyTree.Should().Contain(
            "static partial void AddScheduledJobs(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    }

    [Fact]
    public void GenerateScheduledJobs_UnmatchedModule_EmitsWarning()
    {
        var source = CreateSource(
            """
            namespace Elarion.Abstractions.Modules {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class AppModuleAttribute : System.Attribute {
                    public AppModuleAttribute(string name) { Name = name; }
                    public string Name { get; }
                }
            }

            namespace Sample.Modules {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }
            }

            namespace Other.Jobs {
                public sealed class StrayJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("stray.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELSG010" && d.Severity == DiagnosticSeverity.Warning)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_MatchedModule_DoesNotWarnUnmatched()
    {
        var source = CreateSource(
            """
            namespace Elarion.Abstractions.Modules {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class AppModuleAttribute : System.Attribute {
                    public AppModuleAttribute(string name) { Name = name; }
                    public string Name { get; }
                }
            }

            namespace Sample.Jobs {
                [Elarion.Abstractions.Modules.AppModule("Billing")]
                public static class BillingModule { }

                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELSG010").Should().BeFalse();
    }

    [Fact]
    public void GenerateScheduledJobs_NoModules_DoesNotWarnUnmatched()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """,
            wrapInModule: false);

        var result = Generate(source);

        result.Diagnostics.Any(d => d.Id == "ELSG010").Should().BeFalse();
    }

    [Fact]
    public void GenerateScheduledJobs_UseElarion_EmitsTypedDescriptor()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """,
            "[assembly: Elarion.Abstractions.UseElarion]");

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("Name = \"billing.daily\"");
        generatedSource.Should().Contain("ScheduledJobSchedule.FixedRate(\"1d\", null, true)");
        generatedSource.Should().Contain("MisfirePolicy = global::Elarion.Abstractions.Scheduling.ScheduledJobMisfirePolicy.FireOnce");
    }

    [Fact]
    public void GenerateScheduledJobs_RuntimeJob_EmitsTypedPayloadDescriptor()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed record SendMailPayload {
                    public required string Subject { get; init; }
                }

                [Elarion.Abstractions.Scheduling.ScheduledJob("mail.send")]
                public sealed class SendMailJob : Elarion.Abstractions.Scheduling.IScheduledJob<SendMailPayload> {
                    public System.Threading.Tasks.ValueTask ExecuteAsync(
                        SendMailPayload payload,
                        Elarion.Abstractions.Scheduling.IScheduledJobContext context,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("services.TryAddScoped<global::Sample.Jobs.SendMailJob>();");
        generatedSource.Should().Contain("JobType = typeof(global::Sample.Jobs.SendMailJob)");
        generatedSource.Should().Contain("PayloadType = typeof(global::Sample.Jobs.SendMailPayload)");
        generatedSource.Should().Contain("var typedPayload = (global::Sample.Jobs.SendMailPayload)payload!;");
        generatedSource.Should().Contain("job.ExecuteAsync(typedPayload, context, ct)");
    }

    [Fact]
    public void GenerateScheduledJobs_ResilientJob_EmitsPolicyReference()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Resilience.Resilient("billing-resilience")]
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("ResiliencePolicy = new global::Elarion.Abstractions.Resilience.ResiliencePolicyReference { Name = \"billing-resilience\" }");
    }

    [Fact]
    public void GenerateScheduledJobs_CronJob_EmitsCronSchedule()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob(
                        "billing.nightly",
                        Cron = "0 0 3 * * *",
                        TimeZone = "Europe/Vienna")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("ScheduledJobSchedule.Cron(\"0 0 3 * * *\", \"Europe/Vienna\")");
    }

    [Fact]
    public void GenerateScheduledJobs_DisabledCron_EmitsCronSchedule()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.disabled", Cron = "-")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("ScheduledJobSchedule.Cron(\"-\", null)");
    }

    [Fact]
    public void GenerateScheduledJobs_InitialDelayOnly_EmitsOneTimeSchedule()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class WarmupJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("warmup.once", InitialDelay = "5s")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("ScheduledJobSchedule.Once(\"5s\")");
        generatedSource.Should().Contain("WarmupOnce { get; } = new() { Name = \"warmup.once\" };");
    }

    [Fact]
    public void GenerateScheduledJobs_ReferenceNames_SanitizeInvalidCharactersAndAvoidCollisions()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class ReferenceJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing-daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask FirstAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;

                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask SecondAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;

                    [Elarion.Abstractions.Scheduling.ScheduledJob("123 import", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask NumericAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;

                    [Elarion.Abstractions.Scheduling.ScheduledJob("!!!", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask SymbolsAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source);
        var generatedSource = AllGenerated(result);

        generatedSource.Should().Contain("_ { get; } = new() { Name = \"!!!\" };");
        generatedSource.Should().Contain("_123Import { get; } = new() { Name = \"123 import\" };");
        generatedSource.Should().Contain("BillingDaily { get; } = new() { Name = \"billing-daily\" };");
        generatedSource.Should().Contain("BillingDaily2 { get; } = new() { Name = \"billing.daily\" };");
    }

    [Fact]
    public void GenerateScheduledJobs_MultipleScheduleKinds_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d", Cron = "0 0 3 * * *")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG008"]);

        result.Diagnostics.Any(d => d.Id == "ELSG008" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_MethodWithoutSchedule_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG008"]);

        result.Diagnostics.Any(d => d.Id == "ELSG008" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_CronWithInitialDelay_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", Cron = "0 0 3 * * *", InitialDelay = "5m")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG008"]);

        result.Diagnostics.Any(d => d.Id == "ELSG008" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_InitialDelayOnlyWithRunOnStart_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class WarmupJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("warmup.once", InitialDelay = "5s", RunOnStart = true)]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG008"]);

        result.Diagnostics.Any(d => d.Id == "ELSG008" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_NegativeMaxConcurrentRuns_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d", MaxConcurrentRuns = -1)]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG009"]);

        result.Diagnostics.Any(d => d.Id == "ELSG009" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_InvalidMethodSignature_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(string invalid) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG004"]);

        result.Diagnostics.Any(d => d.Id == "ELSG004" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_GenericRuntimeJob_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                [Elarion.Abstractions.Scheduling.ScheduledJob("generic")]
                public sealed class GenericJob<T> : Elarion.Abstractions.Scheduling.IScheduledJob<T> {
                    public System.Threading.Tasks.ValueTask ExecuteAsync(
                        T payload,
                        Elarion.Abstractions.Scheduling.IScheduledJobContext context,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG005"]);

        result.Diagnostics.Any(d => d.Id == "ELSG005" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_DuplicateNames_EmitsDiagnostic()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class FirstJob {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("duplicate", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }

                public sealed class SecondJob {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("duplicate", FixedRate = "1h")]
                    public System.Threading.Tasks.ValueTask RunAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        var result = Generate(source, assertGeneratedOutputCompiles: false, allowedDiagnosticIds: ["ELSG007"]);

        result.Diagnostics.Any(d => d.Id == "ELSG007" && d.Severity == DiagnosticSeverity.Error)
            .Should().BeTrue();
    }

    [Fact]
    public void GenerateScheduledJobs_IrrelevantEdit_ReusesPipeline()
    {
        var source = CreateSource(
            """
            namespace Sample.Jobs {
                public sealed class BillingJobs {
                    [Elarion.Abstractions.Scheduling.ScheduledJob("billing.daily", FixedRate = "1d")]
                    public System.Threading.Tasks.ValueTask RunAsync(
                        Elarion.Abstractions.Scheduling.IScheduledJobContext context,
                        System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.CompletedTask;
                }
            }
            """);

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new SchedulerRegistrationGenerator(),
            source,
            "ScheduledJobs",
            "ScheduledJobsCombined");
    }

    private static string CreateSource(
        string testSource,
        string assemblyTrigger = "[assembly: Elarion.Abstractions.GenerateScheduledJobs]",
        bool wrapInModule = true)
    {
        // Jobs register only per module, so wrap the `Sample.Jobs` test namespace in a module by default.
        // Skip when the test declares its own module attribute; tests asserting no-module behavior pass false.
        var moduleDeclaration = wrapInModule && !testSource.Contains("AppModule(")
            ? """
            namespace Elarion.Abstractions.Modules {
                [System.AttributeUsage(System.AttributeTargets.Class)]
                public sealed class AppModuleAttribute : System.Attribute {
                    public AppModuleAttribute(string name) { Name = name; }
                    public string Name { get; }
                }
            }

            namespace Sample.Jobs {
                [Elarion.Abstractions.Modules.AppModule("Sample")]
                public static class GeneratedTestModule { }
            }
            """
            : "";

        return $$"""
        {{assemblyTrigger}}

        namespace Elarion.Abstractions {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class GenerateScheduledJobsAttribute : System.Attribute;

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class UseElarionAttribute : System.Attribute;
        }

        namespace Elarion.Abstractions.Scheduling {
            public enum ScheduledJobOverlap {
                Skip,
                Queue,
                AllowConcurrent
            }

            public enum ScheduledJobMisfirePolicy {
                FireOnce,
                Skip,
                CatchUp
            }

            public enum JobPlacement {
                Cluster,
                EveryNode
            }

            [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
            public sealed class ScheduledJobAttribute : System.Attribute {
                public ScheduledJobAttribute(string name) {
                    Name = name;
                }

                public string Name { get; }
                public string? FixedRate { get; init; }
                public string? FixedDelay { get; init; }
                public string? Cron { get; init; }
                public string? TimeZone { get; init; }
                public string? InitialDelay { get; init; }
                public bool RunOnStart { get; init; } = true;
                public string? Group { get; init; }
                public ScheduledJobOverlap Overlap { get; init; } = ScheduledJobOverlap.Skip;
                public ScheduledJobMisfirePolicy MisfirePolicy { get; init; } = ScheduledJobMisfirePolicy.FireOnce;
                public int MaxConcurrentRuns { get; init; }
                public string? Enabled { get; init; }
                public JobPlacement Placement { get; init; } = JobPlacement.Cluster;
            }

            public interface IScheduledJobContext;

            public readonly record struct ScheduledJobReference {
                public required string Name { get; init; }
            }

            public interface IScheduledJob<in TPayload> {
                System.Threading.Tasks.ValueTask ExecuteAsync(
                    TPayload payload,
                    IScheduledJobContext context,
                    System.Threading.CancellationToken ct);
            }

            public delegate System.Threading.Tasks.ValueTask ScheduledJobInvokeDelegate(
                System.IServiceProvider serviceProvider,
                object? payload,
                IScheduledJobContext context,
                System.Threading.CancellationToken ct);

            public sealed record ScheduledJobDescriptor {
                public required string Name { get; init; }
                public System.Type? JobType { get; init; }
                public System.Type? PayloadType { get; init; }
                public ScheduledJobSchedule? Schedule { get; init; }
                public string? Group { get; init; }
                public ScheduledJobOverlap Overlap { get; init; } = ScheduledJobOverlap.Skip;
                public ScheduledJobMisfirePolicy MisfirePolicy { get; init; } = ScheduledJobMisfirePolicy.FireOnce;
                public int MaxConcurrentRuns { get; init; }
                public string? Enabled { get; init; }
                public JobPlacement Placement { get; init; } = JobPlacement.Cluster;
                public Elarion.Abstractions.Resilience.ResiliencePolicyReference? ResiliencePolicy { get; init; }
                public required ScheduledJobInvokeDelegate InvokeAsync { get; init; }
            }

            public sealed record ScheduledJobSchedule {
                public static ScheduledJobSchedule FixedRate(string every, string? initialDelay = null, bool runOnStart = true) => new();
                public static ScheduledJobSchedule FixedDelay(string delay, string? initialDelay = null, bool runOnStart = true) => new();
                public static ScheduledJobSchedule Cron(string expression, string? timeZone = null) => new();
                public static ScheduledJobSchedule Once(string initialDelay) => new();
            }
        }

        namespace Elarion.Abstractions.Resilience {
            public readonly record struct ResiliencePolicyReference {
                public required string Name { get; init; }
            }

            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
            public sealed class ResilientAttribute(string policyName) : System.Attribute {
                public string PolicyName { get; } = policyName;
            }
        }

        {{moduleDeclaration}}

        {{testSource}}
        """;
    }

    private static string AllGenerated(GeneratorDriverRunResult result) =>
        string.Concat(result.GeneratedTrees.Select(tree => tree.GetText().ToString()));

    private static GeneratorDriverRunResult Generate(
        string source,
        bool assertGeneratedOutputCompiles = true,
        string[]? allowedDiagnosticIds = null)
    {
        var allowedIds = new HashSet<string>(allowedDiagnosticIds ?? []);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "SchedulerRegistrationGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new SchedulerRegistrationGenerator(), new ModuleDefaultServicesGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var generatorDiagnostics);
        var result = driver.GetRunResult();

        var unexpectedGeneratorDiagnostics = generatorDiagnostics.Concat(result.Diagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error && !allowedIds.Contains(d.Id));
        unexpectedGeneratorDiagnostics.Should().BeEmpty();

        if (assertGeneratedOutputCompiles)
        {
            outputCompilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty();
        }

        return result;
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
