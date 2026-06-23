using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates DI registration for compile-time scheduled jobs and runtime-schedulable job types.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SchedulerRegistrationGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.Abstractions.GenerateScheduledJobsAttribute";

    private const string ScheduledJobAttributeMetadataName =
        "Elarion.Abstractions.Scheduling.ScheduledJobAttribute";

    private const string ScheduledJobInterfaceMetadataName =
        "Elarion.Abstractions.Scheduling.IScheduledJob`1";

    private const string ResilientAttributeMetadataName =
        "Elarion.Abstractions.Resilience.ResilientAttribute";

    private const string ScheduledJobContextMetadataName =
        "Elarion.Abstractions.Scheduling.IScheduledJobContext";

    private const string CancellationTokenMetadataName =
        "System.Threading.CancellationToken";

    private const string TaskMetadataName =
        "System.Threading.Tasks.Task";

    private const string ValueTaskMetadataName =
        "System.Threading.Tasks.ValueTask";

    private static readonly DiagnosticDescriptor InvalidScheduledJobMethodSignature = new(
        id: "ELSG004",
        title: "Invalid scheduled job method signature",
        messageFormat:
        "Scheduled job method '{0}' must be accessible, non-generic, return Task or ValueTask, and accept only IScheduledJobContext and/or CancellationToken parameters",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GenericScheduledJobType = new(
        id: "ELSG005",
        title: "Generic scheduled job type is not supported",
        messageFormat: "Scheduled job type '{0}' is not supported because it is generic or nested in a generic type",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidRuntimeScheduledJobType = new(
        id: "ELSG006",
        title: "Invalid runtime scheduled job type",
        messageFormat: "Runtime scheduled job type '{0}' must implement exactly one IScheduledJob<TPayload> interface",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateScheduledJobName = new(
        id: "ELSG007",
        title: "Duplicate scheduled job name",
        messageFormat: "Scheduled job name '{0}' is used more than once",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidScheduleSpecification = new(
        id: "ELSG008",
        title: "Invalid schedule specification",
        messageFormat:
        "Scheduled job '{0}' has an invalid schedule: methods must declare exactly one of FixedRate, FixedDelay, Cron, or InitialDelay-only one-time scheduling (classes at most one), and InitialDelay/RunOnStart cannot be combined with Cron",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMaxConcurrentRuns = new(
        id: "ELSG009",
        title: "Invalid scheduled job concurrency",
        messageFormat: "Scheduled job '{0}' has an invalid MaxConcurrentRuns value; it must be 0 or greater",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor JobNotInModule = new(
        id: "ELSG010",
        title: "Scheduled job is not in any module",
        messageFormat:
        "Scheduled job '{0}' is annotated with [ScheduledJob] but its namespace is not under any [AppModule]; "
        + "under a module-bootstrapper host it will not be registered (only the flat Add{Assembly}ScheduledJobs method registers it)",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record ScheduledJobInfo(
        string Name,
        string HintName,
        string Namespace,
        string? JobTypeFqn,
        string? PayloadTypeFqn,
        string? MethodContainingTypeFqn,
        string? MethodName,
        bool IsStaticMethod,
        string ReturnKind,
        EquatableArray<MethodParameterKind> MethodParameters,
        string? ScheduleKind,
        string? ScheduleValue,
        string? TimeZone,
        string? InitialDelay,
        bool RunOnStart,
        string? Group,
        string Overlap,
        string MisfirePolicy,
        int MaxConcurrentRuns,
        string? Enabled,
        string? ResiliencePolicyName);

    private enum MethodParameterKind
    {
        Context,
        CancellationToken
    }

    /// <summary>A discovered job: either a registration model or the diagnostics that rejected it.</summary>
    private sealed record JobResult(ScheduledJobInfo? Job, EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames
    {
        public const string Jobs = "ScheduledJobs";
        public const string Combined = "ScheduledJobsCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // [ScheduledJob] applies to both methods (compile-time jobs) and types (runtime jobs); a single
        // attribute-index pass discovers both, and the transform branches on the target symbol kind.
        var results = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ScheduledJobAttributeMetadataName,
                static (node, _) => node is MethodDeclarationSyntax or ClassDeclarationSyntax,
                static (ctx, ct) => CreateJobResult(ctx, ct))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Jobs);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);
        var assemblyName = context.CompilationProvider.Select(static (compilation, _) => compilation.AssemblyName);

        var combined = results.Combine(modules).Combine(trigger).Combine(assemblyName)
            .WithTrackingName(TrackingNames.Combined);

        // Note 50: The scheduler generator is assembly opt-in, which keeps unrelated projects free of generated registrations.
        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (((results, modules), hasTrigger), assemblyName) = source;
            if (!hasTrigger)
            {
                return;
            }

            foreach (var result in results)
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }

            var jobs = new List<ScheduledJobInfo>();
            foreach (var result in results)
            {
                if (result.Job is not null)
                {
                    jobs.Add(result.Job);
                }
            }

            jobs.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));

            ReportDuplicateNames(jobs, spc);
            if (jobs.Count == 0)
            {
                return;
            }

            // Jobs are registered per module via ConfigureDefaultServices; only the assembly-level typed job
            // references remain at the assembly scope.
            var references = GenerateReferences(assemblyName ?? "Generated", jobs);
            spc.AddSource("ScheduledJobReferences.g.cs", SourceText.From(references, Encoding.UTF8));

            EmitPerModule(spc, modules, jobs);
        });
    }

    private static JobResult? CreateJobResult(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Attributes.Length == 0)
        {
            return null;
        }

        var attribute = ctx.Attributes[0];
        var compilation = ctx.SemanticModel.Compilation;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (ctx.TargetSymbol is IMethodSymbol method)
        {
            var contextType = compilation.GetTypeByMetadataName(ScheduledJobContextMetadataName);
            var cancellationTokenType = compilation.GetTypeByMetadataName(CancellationTokenMetadataName);
            var taskType = compilation.GetTypeByMetadataName(TaskMetadataName);
            var valueTaskType = compilation.GetTypeByMetadataName(ValueTaskMetadataName);
            if (contextType is null || cancellationTokenType is null || taskType is null || valueTaskType is null)
            {
                return null;
            }

            var location = (ctx.TargetNode as MethodDeclarationSyntax)?.Identifier.GetLocation();
            var job = TryCreateMethodJob(
                method, attribute, contextType, cancellationTokenType, taskType, valueTaskType, location, diagnostics);
            return new JobResult(job, diagnostics.ToImmutable());
        }

        if (ctx.TargetSymbol is INamedTypeSymbol type)
        {
            var scheduledJobInterface = compilation.GetTypeByMetadataName(ScheduledJobInterfaceMetadataName);
            if (scheduledJobInterface is null)
            {
                return null;
            }

            var location = (ctx.TargetNode as ClassDeclarationSyntax)?.Identifier.GetLocation();
            var job = TryCreateRuntimeJob(type, attribute, scheduledJobInterface, location, diagnostics);
            return new JobResult(job, diagnostics.ToImmutable());
        }

        return null;
    }

    private static void EmitPerModule(
        SourceProductionContext spc,
        IReadOnlyList<ModuleScanner.Module> modules,
        IReadOnlyList<ScheduledJobInfo> jobs)
    {
        if (modules.Count == 0)
        {
            return;
        }

        var byModule = new Dictionary<ModuleScanner.Module, List<ScheduledJobInfo>>();
        foreach (var job in jobs)
        {
            var module = ModuleScanner.FindBest(job.Namespace, modules);
            if (module is null)
            {
                // Modules exist (guarded above) but this job matches none: the per-module path is the
                // only one a module-bootstrapper host calls, so warn that it would be silently dropped.
                spc.ReportDiagnostic(Diagnostic.Create(JobNotInModule, Location.None, job.Name));
                continue;
            }

            if (!byModule.TryGetValue(module, out var list))
            {
                list = [];
                byModule[module] = list;
            }

            list.Add(job);
        }

        foreach (var kvp in byModule.OrderBy(x => x.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var className = $"{module.Name}ScheduledJobExtensions";
            var methodName = $"Add{module.Name}ScheduledJobs";
            var ns = module.Namespace.Length > 0 ? module.Namespace : null;

            // Per-module methods register only this module's jobs; the typed job references stay assembly-level.
            var source = GenerateRegistration(ns, className, methodName, kvp.Value);
            spc.AddSource($"{module.Name}ScheduledJobExtensions.g.cs", SourceText.From(source, Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddScheduledJobsMethod,
                "ScheduledJobs",
                $"{nsPrefix}{className}.{methodName}(services);");
        }
    }

    private static string GetNamespace(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;

    private static ScheduledJobInfo? TryCreateMethodJob(
        IMethodSymbol method,
        AttributeData attribute,
        INamedTypeSymbol contextType,
        INamedTypeSymbol cancellationTokenType,
        INamedTypeSymbol taskType,
        INamedTypeSymbol valueTaskType,
        Location? location,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var type = method.ContainingType;
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        if (method.TypeParameters.Length > 0 ||
            IsGenericOrNestedInGenericType(type) ||
            !IsAccessible(method.DeclaredAccessibility) ||
            !IsAccessible(type.DeclaredAccessibility) ||
            !IsSupportedReturnType(method.ReturnType, taskType, valueTaskType))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidScheduledJobMethodSignature,
                location,
                method.ToDisplayString(fmt)));
            return null;
        }

        var parameters = ImmutableArray.CreateBuilder<MethodParameterKind>();
        var seenContext = false;
        var seenCancellationToken = false;
        foreach (var parameter in method.Parameters)
        {
            // Note 52: Only framework-owned context and CancellationToken parameters are accepted so invocation stays source-generated.
            if (SymbolEqualityComparer.Default.Equals(parameter.Type, contextType) && !seenContext)
            {
                parameters.Add(MethodParameterKind.Context);
                seenContext = true;
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenType) && !seenCancellationToken)
            {
                parameters.Add(MethodParameterKind.CancellationToken);
                seenCancellationToken = true;
                continue;
            }

            diagnostics.Add(DiagnosticInfo.Create(
                InvalidScheduledJobMethodSignature,
                location,
                method.ToDisplayString(fmt)));
            return null;
        }

        if (!TryGetScheduleSpec(attribute, scheduleRequired: true, out var scheduleKind, out var scheduleValue))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidScheduleSpecification,
                location,
                GetRequiredName(attribute)));
            return null;
        }

        var maxConcurrentRuns = GetIntNamedArgument(attribute, "MaxConcurrentRuns", 0);
        if (maxConcurrentRuns < 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidMaxConcurrentRuns,
                location,
                GetRequiredName(attribute)));
            return null;
        }

        return new ScheduledJobInfo(
            Name: GetRequiredName(attribute),
            HintName: GetHintName(type) + "_" + method.Name,
            Namespace: GetNamespace(type),
            JobTypeFqn: null,
            PayloadTypeFqn: null,
            MethodContainingTypeFqn: type.ToDisplayString(fmt),
            MethodName: method.Name,
            IsStaticMethod: method.IsStatic,
            ReturnKind: SymbolEqualityComparer.Default.Equals(method.ReturnType, taskType) ? "Task" : "ValueTask",
            MethodParameters: parameters.ToImmutable(),
            ScheduleKind: scheduleKind,
            ScheduleValue: scheduleValue,
            TimeZone: GetStringNamedArgument(attribute, "TimeZone"),
            InitialDelay: GetStringNamedArgument(attribute, "InitialDelay"),
            RunOnStart: GetBoolNamedArgument(attribute, "RunOnStart", true),
            Group: GetStringNamedArgument(attribute, "Group"),
            Overlap: GetOverlapNamedArgument(attribute),
            MisfirePolicy: GetMisfirePolicyNamedArgument(attribute),
            MaxConcurrentRuns: maxConcurrentRuns,
            Enabled: GetStringNamedArgument(attribute, "Enabled"),
            ResiliencePolicyName: GetResiliencePolicyName(method) ?? GetResiliencePolicyName(type));
    }

    private static ScheduledJobInfo? TryCreateRuntimeJob(
        INamedTypeSymbol type,
        AttributeData attribute,
        INamedTypeSymbol scheduledJobInterface,
        Location? location,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        if (IsGenericOrNestedInGenericType(type))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                GenericScheduledJobType,
                location,
                type.ToDisplayString(fmt)));
            return null;
        }

        if (!IsAccessible(type.DeclaredAccessibility))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidRuntimeScheduledJobType,
                location,
                type.ToDisplayString(fmt)));
            return null;
        }

        var matchingInterfaces = type.AllInterfaces
            .Where(candidate => SymbolEqualityComparer.Default.Equals(
                candidate.OriginalDefinition,
                scheduledJobInterface))
            .ToArray();
        // Note 53: Runtime-scheduled jobs still need exactly one typed payload so the generated delegate can cast safely.
        if (matchingInterfaces.Length != 1 || matchingInterfaces[0].TypeArguments.Length != 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidRuntimeScheduledJobType,
                location,
                type.ToDisplayString(fmt)));
            return null;
        }

        if (!TryGetScheduleSpec(attribute, scheduleRequired: false, out var scheduleKind, out var scheduleValue))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidScheduleSpecification,
                location,
                GetRequiredName(attribute)));
            return null;
        }

        var maxConcurrentRuns = GetIntNamedArgument(attribute, "MaxConcurrentRuns", 0);
        if (maxConcurrentRuns < 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidMaxConcurrentRuns,
                location,
                GetRequiredName(attribute)));
            return null;
        }

        return new ScheduledJobInfo(
            Name: GetRequiredName(attribute),
            HintName: GetHintName(type),
            Namespace: GetNamespace(type),
            JobTypeFqn: type.ToDisplayString(fmt),
            PayloadTypeFqn: matchingInterfaces[0].TypeArguments[0].ToDisplayString(fmt),
            MethodContainingTypeFqn: null,
            MethodName: null,
            IsStaticMethod: false,
            ReturnKind: "ValueTask",
            MethodParameters: ImmutableArray<MethodParameterKind>.Empty,
            ScheduleKind: scheduleKind,
            ScheduleValue: scheduleValue,
            TimeZone: GetStringNamedArgument(attribute, "TimeZone"),
            InitialDelay: GetStringNamedArgument(attribute, "InitialDelay"),
            RunOnStart: GetBoolNamedArgument(attribute, "RunOnStart", true),
            Group: GetStringNamedArgument(attribute, "Group"),
            Overlap: GetOverlapNamedArgument(attribute),
            MisfirePolicy: GetMisfirePolicyNamedArgument(attribute),
            MaxConcurrentRuns: maxConcurrentRuns,
            Enabled: GetStringNamedArgument(attribute, "Enabled"),
            ResiliencePolicyName: GetResiliencePolicyName(type));
    }

    /// <summary>
    /// Extracts the schedule kind and value. Methods require exactly one of FixedRate,
    /// FixedDelay, Cron, or InitialDelay-only one-time scheduling; classes allow none
    /// (runtime-only jobs). InitialDelay and an explicit RunOnStart are rejected
    /// together with Cron.
    /// </summary>
    private static bool TryGetScheduleSpec(
        AttributeData attribute,
        bool scheduleRequired,
        out string? scheduleKind,
        out string? scheduleValue)
    {
        scheduleKind = null;
        scheduleValue = null;

        var fixedRate = GetStringNamedArgument(attribute, "FixedRate");
        var fixedDelay = GetStringNamedArgument(attribute, "FixedDelay");
        var cron = GetStringNamedArgument(attribute, "Cron");
        var initialDelay = GetStringNamedArgument(attribute, "InitialDelay");
        var hasRunOnStart = HasNamedArgument(attribute, "RunOnStart");

        var specified = 0;
        if (fixedRate is not null)
        {
            (scheduleKind, scheduleValue) = ("FixedRate", fixedRate);
            specified++;
        }

        if (fixedDelay is not null)
        {
            (scheduleKind, scheduleValue) = ("FixedDelay", fixedDelay);
            specified++;
        }

        if (cron is not null)
        {
            (scheduleKind, scheduleValue) = ("Cron", cron);
            specified++;
        }

        if (specified > 1)
        {
            return false;
        }

        if (cron is not null && (initialDelay is not null || hasRunOnStart))
        {
            return false;
        }

        if (specified == 0 && initialDelay is not null)
        {
            if (hasRunOnStart)
            {
                return false;
            }

            (scheduleKind, scheduleValue) = ("OneTime", initialDelay);
            return true;
        }

        if (specified == 0 && scheduleRequired)
        {
            return false;
        }

        return true;
    }

    private static void ReportDuplicateNames(
        IReadOnlyList<ScheduledJobInfo> jobs,
        SourceProductionContext spc)
    {
        foreach (var group in jobs.GroupBy(job => job.Name, StringComparer.Ordinal))
        {
            if (group.Count() < 2)
            {
                continue;
            }

            spc.ReportDiagnostic(Diagnostic.Create(DuplicateScheduledJobName, Location.None, group.Key));
        }
    }

    private static string GenerateReferences(string assemblyName, IReadOnlyList<ScheduledJobInfo> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.SchedulerRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        var ns = TryGetNamespaceFromAssemblyName(assemblyName);
        if (ns is not null)
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        AppendJobReferences(sb, SanitizeIdentifier(assemblyName) + "ScheduledJobRegistration", jobs);
        return sb.ToString();
    }

    private static string GenerateRegistration(
        string? ns,
        string className,
        string methodName,
        IReadOnlyList<ScheduledJobInfo> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.SchedulerRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine();
        if (ns is not null)
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {methodName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");

        foreach (var job in jobs.OrderBy(job => job.HintName, StringComparer.Ordinal))
        {
            if (job.JobTypeFqn is not null)
            {
                sb.AppendLine($"        services.TryAddScoped<{job.JobTypeFqn}>();");
            }

            if (job.MethodContainingTypeFqn is not null && !job.IsStaticMethod)
            {
                sb.AppendLine($"        services.TryAddScoped<{job.MethodContainingTypeFqn}>();");
            }
        }

        foreach (var job in jobs)
        {
            AppendDescriptorRegistration(sb, job);
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendDescriptorRegistration(StringBuilder sb, ScheduledJobInfo job)
    {
        sb.AppendLine();
        sb.AppendLine("        services.AddSingleton(new global::Elarion.Abstractions.Scheduling.ScheduledJobDescriptor");
        sb.AppendLine("        {");
        sb.AppendLine($"            Name = \"{EscapeString(job.Name)}\",");
        if (job.JobTypeFqn is not null)
        {
            sb.AppendLine($"            JobType = typeof({job.JobTypeFqn}),");
        }

        if (job.PayloadTypeFqn is not null)
        {
            sb.AppendLine($"            PayloadType = typeof({job.PayloadTypeFqn}),");
        }

        if (job.ScheduleKind is null)
        {
            sb.AppendLine("            Schedule = null,");
        }
        else if (job.ScheduleKind == "Cron")
        {
            var timeZone = job.TimeZone is null ? "null" : $"\"{EscapeString(job.TimeZone)}\"";
            // Note 54: The emitted descriptor stores metadata only; the runtime scheduler owns actual due-time calculation.
            sb.AppendLine(
                $"            Schedule = global::Elarion.Abstractions.Scheduling.ScheduledJobSchedule.Cron(\"{EscapeString(job.ScheduleValue!)}\", {timeZone}),");
        }
        else if (job.ScheduleKind == "OneTime")
        {
            sb.AppendLine(
                $"            Schedule = global::Elarion.Abstractions.Scheduling.ScheduledJobSchedule.Once(\"{EscapeString(job.ScheduleValue!)}\"),");
        }
        else
        {
            var initialDelay = job.InitialDelay is null ? "null" : $"\"{EscapeString(job.InitialDelay)}\"";
            sb.AppendLine(
                $"            Schedule = global::Elarion.Abstractions.Scheduling.ScheduledJobSchedule.{job.ScheduleKind}(\"{EscapeString(job.ScheduleValue!)}\", {initialDelay}, {ToCSharpBool(job.RunOnStart)}),");
        }

        sb.AppendLine(job.Group is null
            ? "            Group = null,"
            : $"            Group = \"{EscapeString(job.Group)}\",");
        sb.AppendLine($"            Overlap = global::Elarion.Abstractions.Scheduling.ScheduledJobOverlap.{job.Overlap},");
        sb.AppendLine($"            MisfirePolicy = global::Elarion.Abstractions.Scheduling.ScheduledJobMisfirePolicy.{job.MisfirePolicy},");
        sb.AppendLine($"            MaxConcurrentRuns = {job.MaxConcurrentRuns},");
        sb.AppendLine(job.Enabled is null
            ? "            Enabled = null,"
            : $"            Enabled = \"{EscapeString(job.Enabled)}\",");
        sb.AppendLine(job.ResiliencePolicyName is null
            ? "            ResiliencePolicy = null,"
            : $"            ResiliencePolicy = new global::Elarion.Abstractions.Resilience.ResiliencePolicyReference {{ Name = \"{EscapeString(job.ResiliencePolicyName)}\" }},");
        AppendInvokeDelegate(sb, job);
        sb.AppendLine("        });");
    }

    private static void AppendInvokeDelegate(StringBuilder sb, ScheduledJobInfo job)
    {
        if (job.JobTypeFqn is not null && job.PayloadTypeFqn is not null)
        {
            sb.AppendLine("            InvokeAsync = static (serviceProvider, payload, context, ct) =>");
            sb.AppendLine("            {");
            // Note 55: Runtime payloads are typed at registration time, avoiding reflection when the scheduler invokes them.
            sb.AppendLine($"                var typedPayload = ({job.PayloadTypeFqn})payload!;");
            sb.AppendLine($"                var job = serviceProvider.GetRequiredService<{job.JobTypeFqn}>();");
            sb.AppendLine("                return job.ExecuteAsync(typedPayload, context, ct);");
            sb.AppendLine("            }");
            return;
        }

        var receiver = job.IsStaticMethod
            ? job.MethodContainingTypeFqn!
            : "job";
        var arguments = string.Join(", ", job.MethodParameters.Select(parameter => parameter switch
        {
            MethodParameterKind.Context => "context",
            _ => "ct"
        }));
        var invocation = $"{receiver}.{job.MethodName}({arguments})";

        sb.AppendLine("            InvokeAsync = static (serviceProvider, payload, context, ct) =>");
        sb.AppendLine("            {");
        if (!job.IsStaticMethod)
        {
            sb.AppendLine($"                var job = serviceProvider.GetRequiredService<{job.MethodContainingTypeFqn}>();");
        }

        if (job.ReturnKind == "Task")
        {
            sb.AppendLine($"                return new global::System.Threading.Tasks.ValueTask({invocation});");
        }
        else
        {
            sb.AppendLine($"                return {invocation};");
        }

        sb.AppendLine("            }");
    }

    private static void AppendJobReferences(
        StringBuilder sb,
        string registrationType,
        IReadOnlyList<ScheduledJobInfo> jobs)
    {
        sb.AppendLine();
        sb.AppendLine($"public static class {registrationType}JobReferences");
        sb.AppendLine("{");
        foreach (var (propertyName, jobName) in CreateReferenceNames(jobs))
        {
            sb.AppendLine(
                $"    public static global::Elarion.Abstractions.Scheduling.ScheduledJobReference {propertyName} {{ get; }} = new() {{ Name = \"{EscapeString(jobName)}\" }};");
        }

        sb.AppendLine("}");
    }

    private static string? GetResiliencePolicyName(ISymbol symbol)
    {
        var attribute = symbol.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == ResilientAttributeMetadataName);
        if (attribute is null ||
            attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Value is not string policyName ||
            string.IsNullOrWhiteSpace(policyName))
        {
            return null;
        }

        return policyName;
    }

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

    private static bool HasNamedArgument(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetOverlapNamedArgument(AttributeData attribute)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key != "Overlap" || argument.Value.Value is not int value)
            {
                continue;
            }

            return value switch
            {
                1 => "Queue",
                2 => "AllowConcurrent",
                _ => "Skip"
            };
        }

        return "Skip";
    }

    private static string GetMisfirePolicyNamedArgument(AttributeData attribute)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            // Note 56: Attribute enum values arrive from Roslyn as integers, so the generator maps them back to source names.
            if (argument.Key != "MisfirePolicy" || argument.Value.Value is not int value)
            {
                continue;
            }

            return value switch
            {
                1 => "Skip",
                2 => "CatchUp",
                _ => "FireOnce"
            };
        }

        return "FireOnce";
    }

    private static bool IsSupportedReturnType(
        ITypeSymbol returnType,
        INamedTypeSymbol taskType,
        INamedTypeSymbol valueTaskType) =>
        SymbolEqualityComparer.Default.Equals(returnType, taskType) ||
        SymbolEqualityComparer.Default.Equals(returnType, valueTaskType);

    private static bool IsAccessible(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Internal;

    private static bool IsGenericOrNestedInGenericType(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetHintName(INamedTypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty)
            .Replace(".", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace(",", "_")
            .Replace(" ", string.Empty);

    private static string? TryGetNamespaceFromAssemblyName(string assemblyName)
    {
        var parts = assemblyName.Split('.');
        if (parts.Length == 0 || parts.Any(part => !IsValidIdentifier(part)))
        {
            return null;
        }

        return string.Join(".", parts);
    }

    private static string SanitizeIdentifier(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
        }

        if (sb.Length == 0 || !IsIdentifierStart(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static string EscapeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ToCSharpBool(bool value) => value ? "true" : "false";

    private static IReadOnlyList<(string PropertyName, string JobName)> CreateReferenceNames(IReadOnlyList<ScheduledJobInfo> jobs)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var references = new List<(string PropertyName, string JobName)>(jobs.Count);
        foreach (var job in jobs.OrderBy(job => job.Name, StringComparer.Ordinal))
        {
            var baseName = ToPascalIdentifier(job.Name);
            used.TryGetValue(baseName, out var count);
            used[baseName] = count + 1;
            references.Add((count == 0 ? baseName : baseName + (count + 1).ToString(), job.Name));
        }

        return references;
    }

    private static string ToPascalIdentifier(string value)
    {
        var sb = new StringBuilder(value.Length);
        var upperNext = true;
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upperNext = true;
                continue;
            }

            sb.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }

        if (sb.Length == 0)
        {
            return "_";
        }

        if (!IsIdentifierStart(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }
}
