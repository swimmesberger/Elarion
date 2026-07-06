using BenchmarkDotNet.Running;

// Run all benchmarks:
//   dotnet run --project tests/Elarion.Benchmarks -c Release
// Run a subset / quick smoke:
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*ActorCall*"
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*" --job short
BenchmarkSwitcher.FromAssembly(typeof(Elarion.Benchmarks.Actors.ActorCallBenchmarks).Assembly).Run(args);
