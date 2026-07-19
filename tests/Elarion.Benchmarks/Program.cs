using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.dotTrace;
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
var config = DefaultConfig.Instance;
if (Environment.GetEnvironmentVariable("ELARION_BENCH_DOTTRACE") is "1" or "true")
    config = config.AddDiagnoser(new DotTraceDiagnoser());

BenchmarkSwitcher.FromAssembly(typeof(Elarion.Benchmarks.Actors.ActorCallBenchmarks).Assembly).Run(args, config);
