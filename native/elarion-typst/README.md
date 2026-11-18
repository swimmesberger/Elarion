# elarion-typst (prototype)

The native library behind `Elarion.Typst.Native`: Typst 0.15.1 compiled into a `cdylib` with a small C ABI. See
[ADR-0094](../../docs/decisions/0094-in-process-typst-engine.md) for the design and the measurements.

```bash
cargo build --release
```

Building `src/Elarion.Typst.Native` (or its tests) runs this for the host platform and copies the library next to the
assemblies. Set `ElarionTypstNativeBuild=false` to skip the cargo step when the library is supplied otherwise.

The ABI is defined in `src/lib.rs` and mirrored in `src/Elarion.Typst.Native/Interop/NativeMethods.cs`. Change both
together and bump `ABI_VERSION` / `NativeMethods.AbiVersion`; the .NET side refuses a library that reports another
version.

Measurements: `tests/Elarion.Typst.Native.Benchmarks` (see its `Program.cs`). Why in-process parallel compilation
is slower than the CLI today, and the upstream comemo change that would fix most of it:
[research/typst-in-process-concurrency.md](../../docs/decisions/research/typst-in-process-concurrency.md).
