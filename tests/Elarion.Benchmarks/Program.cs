using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.dotTrace;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

// Run all benchmarks:
//   dotnet run --project tests/Elarion.Benchmarks -c Release
// Run a subset / quick smoke:
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*ActorCall*"
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*" --job short
//
// Profiling (opt-in, cross-platform / macOS — always pair with a tight --filter so the profiled run stays small):
//
//   Flamegraph (speedscope) via EventPipeProfiler — built into BenchmarkDotNet, nothing extra to attach:
//     dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*Ask*" --profiler EP
//     # writes a .nettrace under BenchmarkDotNet.Artifacts/; convert it to a flamegraph:
//     #   dotnet tool install -g dotnet-trace                                  (once)
//     #   dotnet-trace convert <file>.nettrace --format speedscope             → open the .json on https://speedscope.app
//
//   dotTrace snapshot — DotTraceDiagnoser, opt-in via the env var below (auto-downloads the JetBrains CLI on first run):
//     ELARION_BENCH_DOTTRACE=1 dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*Ask*"
//     # writes a .dtt snapshot under BenchmarkDotNet.Artifacts/ — open it in JetBrains dotTrace / Rider.
//
//   Runtime async A/B (.NET 11) — runs every selected benchmark twice on net11.0: once as the repository builds by
//   default (runtime async, the baseline) and once rebuilt with compiler-generated state machines
//   (/p:ElarionRuntimeAsync=false, which flows to every referenced Elarion project; see Directory.Build.targets, ADR-0092):
//     ELARION_BENCH_RUNTIME_ASYNC=compare dotnet run --project tests/Elarion.Benchmarks -c Release -f net11.0 -- --filter "*HandlerPipeline*"
//     # compare-short is a quicker pass (1 launch, 3 warmup and 10 measured iterations) for wide filters.
IConfig config = DefaultConfig.Instance;
if (Environment.GetEnvironmentVariable("ELARION_BENCH_DOTTRACE") is "1" or "true")
    config = config.AddDiagnoser(new DotTraceDiagnoser());

if (Environment.GetEnvironmentVariable("ELARION_BENCH_RUNTIME_ASYNC") is { } runtimeAsyncMode
    && runtimeAsyncMode.StartsWith("compare", StringComparison.Ordinal)) {
    var job = runtimeAsyncMode == "compare-short"
        ? Job.Default.WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(10)
        : Job.Default;
    config = config
        .AddJob(job.WithId("RuntimeAsync").AsBaseline())
        .AddJob(job.WithArguments([new MsBuildArgument("/p:ElarionRuntimeAsync=false")]).WithId("StateMachines"));
}

BenchmarkSwitcher.FromAssembly(typeof(Elarion.Benchmarks.Actors.ActorCallBenchmarks).Assembly).Run(args, config);
