using System.Diagnostics;
using Elarion.Typst;
using Elarion.Typst.Native;
using Microsoft.Extensions.Logging.Abstractions;

// Measures the CLI engine against the in-process engine on the shared test package and two generated documents: a
// 2,000-row table ("large") and 60 sections with an outline, cross-references and a "page X of Y" footer ("intro",
// which needs several layout passes). Every document carries different data, so memoization cannot return a cached
// layout (identical requests make the in-process engine look ~10x faster than it is).
//
// Prefer CPU per document over docs/s: on a loaded machine the OS shares CPU per runnable thread, so wall-clock
// throughput mostly measures how many threads each engine runs. Interleave configurations and repeat rounds.
// Run with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 so numbers print with a dot.
//
//   dotnet run -c Release --project tests/Elarion.Typst.Native.Benchmarks                    full comparison
//   ... -- cpu    <k> <n> [large|intro]   in-process, k concurrent: docs/s, CPU per document, cores busy
//   ... -- clicpu <k> <n> [large|intro]   the same for the CLI engine (child-process CPU via getrusage)
//   ... -- cpuN   <k> <n> [large|intro]   k separate engines (own fonts and package cache) in one process
//   ... -- sweep                          in-process scaling, 1..14 concurrent
//   ... -- loop [k]                       endless load, for sample(1) profiling
//   ... -- worker <n> <utc-start>         one process compiling n documents from a shared start time; run 14 at once
//                                         for the multi-process comparison (prints end time and CPU seconds)
var toolchainDir = Path.Combine(AppContext.BaseDirectory, "typst");
args = ["", .. args];
var logo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
TypstOptions Options(int concurrency) => new() { ToolchainDirectory = toolchainDir, MaxConcurrency = concurrency, WorkingDirectory = Path.Combine(Path.GetTempPath(), "typst-bench") };

var counter = 0;
TypstCompileRequest Small() { var n = Interlocked.Increment(ref counter); return new TypstCompileRequest {
    Source = TypstSource.Package("@local/elarion-test:1.0.0", "invoice"),
    Data = TypstData.FromJson("{\"number\":\"2026-0042\",\"items\":[{\"name\":\"Consulting " + n + "\",\"amount\":\"" + n + ".00\"},{\"name\":\"Travel\",\"amount\":\"80.00\"}]}"),
    Assets = [new TypstAsset("logo.png", logo)],
    Inputs = new Dictionary<string, string> { ["lang"] = "de" }
}; }
TypstCompileRequest Large() { var n = Interlocked.Increment(ref counter); return new TypstCompileRequest {
    Source = TypstSource.Main("""
        #let data = json("/data.json")
        #set page(paper: "a4")
        #set text(size: 9pt)
        = Report
        #table(columns: (1fr, auto, auto, auto), ..data.rows.map(r => (r.a, r.b, r.c, r.d)).flatten())
        """),
    Data = TypstData.FromJson("{\"rows\":[" + string.Join(",", Enumerable.Range(1, 2000).Select(i => "{\"a\":\"Position " + i + "\",\"b\":\"" + (i * n) + "\",\"c\":\"" + (i * 1.5m + n) + "\",\"d\":\"x\"}")) + "]}")
}; }
TypstCompileRequest Intro() { var n = Interlocked.Increment(ref counter); return new TypstCompileRequest {
    Source = TypstSource.Main("""
        #let data = json("/data.json")
        #set page(paper: "a4", footer: context [Page #counter(page).display() of #counter(page).final().first()])
        #set heading(numbering: "1.1")
        #outline()
        #for (i, s) in data.sections.enumerate() [
          #heading(level: 1)[#s.title] #label("s" + str(i))
          #s.text See also @#("s" + str(calc.rem(i + 1, data.sections.len()))).
          #table(columns: 3, ..s.rows.map(r => (r.a, r.b, r.c)).flatten())
        ]
        """),
    Data = TypstData.FromJson("{\"sections\":[" + string.Join(",", Enumerable.Range(1, 60).Select(i =>
        "{\"title\":\"Section " + i + " / " + n + "\",\"text\":\"" + string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet " + n + ". ", 8)) + "\",\"rows\":[" +
        string.Join(",", Enumerable.Range(1, 20).Select(r => "{\"a\":\"" + r + "\",\"b\":\"" + (r * i + n) + "\",\"c\":\"x\"}")) + "]}")) + "]}")
}; }
var unusedSmall = new TypstCompileRequest {
    Source = TypstSource.Package("@local/elarion-test:1.0.0", "invoice"),
    Data = TypstData.FromJson("""{"number":"2026-0042","items":[{"name":"Consulting","amount":"1,200.00"},{"name":"Travel","amount":"80.00"}]}"""),
    Assets = [new TypstAsset("logo.png", logo)],
    Inputs = new Dictionary<string, string> { ["lang"] = "de" }
};
var rows = string.Join(",", Enumerable.Range(1, 2000).Select(i => $$"""{"a":"Position {{i}}","b":"{{i * 3}}","c":"{{i * 1.5m:0.00}}","d":"{{i * 4.5m:0.00}}"}"""));
var large = new TypstCompileRequest {
    Source = TypstSource.Main("""
        #let data = json("/data.json")
        #set page(paper: "a4")
        #set text(size: 9pt)
        = Report
        #table(columns: (1fr, auto, auto, auto), ..data.rows.map(r => (r.a, r.b, r.c, r.d)).flatten())
        """),
    Data = TypstData.FromJson("{\"rows\":[" + rows + "]}")
};

var sw = Stopwatch.StartNew();
var nativeOptions = Options(Environment.ProcessorCount);
var engine = new NativeTypstEngine(nativeOptions, new TypstToolchain(nativeOptions));
Console.WriteLine($"native engine startup (load library + fonts): {sw.Elapsed.TotalMilliseconds:F1} ms");

ITypstCompiler Cli(int c) { var o = Options(c); return new TypstCompiler(o, new TypstToolchain(o), NullLogger<TypstCompiler>.Instance); }
ITypstCompiler Native(int c) => new NativeTypstCompiler(Options(c), engine, NullLogger<NativeTypstCompiler>.Instance);

async Task<double> Once(ITypstCompiler compiler, Func<TypstCompileRequest> request) {
    var t = Stopwatch.StartNew();
    await using var pdf = await compiler.CompilePdfAsync(request());
    return t.Elapsed.TotalMilliseconds;
}

async Task Sequential(string name, ITypstCompiler compiler, Func<TypstCompileRequest> request, int n) {
    var first = await Once(compiler, request);
    for (var i = 0; i < 3; i++) await Once(compiler, request);
    var samples = new List<double>();
    for (var i = 0; i < n; i++) samples.Add(await Once(compiler, request));
    samples.Sort();
    Console.WriteLine($"{name,-28} first {first,8:F1} ms | median {samples[n / 2],7:F1} ms | p95 {samples[(int)(n * 0.95)],7:F1} ms");
}

async Task Parallel(string name, ITypstCompiler compiler, Func<TypstCompileRequest> request, int documents) {
    await Once(compiler, request);
    var t = Stopwatch.StartNew();
    var pdfs = await Task.WhenAll(Enumerable.Range(0, documents).Select(_ => compiler.CompilePdfAsync(request())));
    var elapsed = t.Elapsed.TotalSeconds;
    foreach (var pdf in pdfs) await pdf.DisposeAsync();
    Console.WriteLine($"{name,-28} {documents} documents in {elapsed:F2} s = {documents / elapsed,6:F1} docs/s");
}

if (args.Length > 3 && args[1] == "cpuN") {
    // k engines (each with its own fonts and package cache), one compilation at a time per engine.
    var k = int.Parse(args[2]); var n = int.Parse(args[3]);
    Func<TypstCompileRequest> doc = args.Length > 4 && args[4] == "intro" ? Intro : Large;
    var compilers = Enumerable.Range(0, k).Select(_ => {
        var o = Options(1);
        var e = new NativeTypstEngine(o, new TypstToolchain(o));
        return (ITypstCompiler)new NativeTypstCompiler(o, e, NullLogger<NativeTypstCompiler>.Instance);
    }).ToArray();
    foreach (var engineCompiler in compilers) await Once(engineCompiler, doc);
    var cpu0 = Cpu.Self();
    var t = Stopwatch.StartNew();
    await Task.WhenAll(compilers.Select(async engineCompiler => { for (var i = 0; i < n / k; i++) await Once(engineCompiler, doc); }));
    var wall = t.Elapsed.TotalSeconds;
    var cpu = (Cpu.Self() - cpu0).TotalSeconds;
    Console.WriteLine($"cpuN   {(args.Length > 4 ? args[4] : "large"),-5} k={k,2} {n / wall,6:F1} docs/s | cpu/doc {cpu / n * 1000,6:F0} ms | cores busy {cpu / wall,5:F1}");
    return;
}
if (args.Length > 3 && (args[1] == "cpu" || args[1] == "clicpu")) {
    var k = int.Parse(args[2]); var n = int.Parse(args[3]);
    var compiler = args[1] == "cpu" ? Native(k) : Cli(k);
    Func<TypstCompileRequest> doc = args.Length > 4 && args[4] == "intro" ? Intro : Large;
    for (var i = 0; i < 2; i++) await Once(compiler, doc);
    var cpu0 = Cpu.Self() + Cpu.Children();
    var t = Stopwatch.StartNew();
    var pdfs = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => compiler.CompilePdfAsync(doc())));
    var wall = t.Elapsed.TotalSeconds;
    var cpu = (Cpu.Self() + Cpu.Children() - cpu0).TotalSeconds;
    foreach (var pdf in pdfs) await pdf.DisposeAsync();
    Console.WriteLine($"{args[1],-6} {(args.Length > 4 ? args[4] : "large"),-5} k={k,2} {n / wall,6:F1} docs/s | cpu/doc {cpu / n * 1000,6:F0} ms | cores busy {cpu / wall,5:F1}");
    return;
}
if (args.Length > 2 && args[1] == "worker") {
    // One worker process: warm up, wait for a shared start time, then compile N large documents sequentially.
    var compiler = Native(1);
    await Once(compiler, Large);
    var startAt = DateTime.Parse(args[3], null, System.Globalization.DateTimeStyles.RoundtripKind);
    while (DateTime.UtcNow < startAt) await Task.Delay(1);
    var c0 = Cpu.Self();
    var t = Stopwatch.StartNew();
    for (var i = 0; i < int.Parse(args[2]); i++) await Once(compiler, Large);
    Console.WriteLine($"{(startAt + t.Elapsed):O} {(Cpu.Self() - c0).TotalSeconds:F4}");
    return;
}
if (args.Length > 1 && args[1] == "loop") {
    var compiler = Native(args.Length > 2 ? int.Parse(args[2]) : 14);
    Console.WriteLine($"pid {Environment.ProcessId}");
    while (true) await Parallel("native large loop", compiler, Large, 24);
}
if (args.Length > 1) {
    foreach (var k in new[] { 1, 2, 4, 8, 14 }) await Parallel($"native large x{k} parallel", Native(k), Large, 24);
    return;
}
Console.WriteLine($"cores: {Environment.ProcessorCount}");
await Sequential("CLI    small invoice", Cli(1), Small, 40);
await Sequential("native small invoice", Native(1), Small, 40);
await Sequential("CLI    2000-row report", Cli(1), Large, 10);
await Sequential("native 2000-row report", Native(1), Large, 10);
var c = Environment.ProcessorCount;
await Parallel($"CLI    small x{c} parallel", Cli(c), Small, 200);
await Parallel($"native small x{c} parallel", Native(c), Small, 200);
await Parallel($"CLI    large x{c} parallel", Cli(c), Large, 24);
await Parallel($"native large x{c} parallel", Native(c), Large, 24);
var proc = Process.GetCurrentProcess(); proc.Refresh();
Console.WriteLine($"working set after all runs: {proc.WorkingSet64 / 1024 / 1024} MB");

static class Cpu {
    [global::System.Runtime.InteropServices.DllImport("libc")] static extern int getrusage(int who, out RUsage usage);
    [global::System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TimeVal { public long Seconds; public int Micro; }
    [global::System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RUsage { public TimeVal Usr; public TimeVal Sys; public long r1, r2, r3, r4, r5, r6, r7, r8, r9, r10, r11, r12, r13, r14; }
    static TimeSpan Get(int who) { getrusage(who, out var u); return TimeSpan.FromSeconds(u.Usr.Seconds + u.Usr.Micro / 1e6 + u.Sys.Seconds + u.Sys.Micro / 1e6); }
    public static TimeSpan Self() => Get(0);
    public static TimeSpan Children() => Get(-1);
}
