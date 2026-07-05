# CLAUDE.md

## Cross-platform

This library targets `net10.0` and must work optimally on **all** platforms
.NET supports: x64 (Windows, Linux, macOS), ARM64 (Windows on ARM, Apple
Silicon, Linux ARM64). Do not use platform-specific intrinsics
(`Sse2`, `AdvSimd`) directly — use the portable `Vector<T>` or
`Vector128/256/512` with `IsHardwareAccelerated` guards.

## Build & test

```bash
dotnet build -c Release
dotnet test tests/SharpAstro.Fonts.Tests -c Release
```

## Benchmarks

```bash
# Quick iteration (seconds per benchmark):
dotnet run --project benchmarks/SharpAstro.Fonts.Benchmarks -c Release -- --filter '*ClassName*' --job short

# Full statistical run (minutes):
dotnet run --project benchmarks/SharpAstro.Fonts.Benchmarks -c Release -- --filter '*'
```

## Coding patterns

### Immutable data types — use `ImmutableArray<T>`, not `internal T[]` accessors

When a type is documented as immutable and thread-safe (e.g. `Outline`),
store backing data as `ImmutableArray<T>` rather than `T[]`. This gives
compile-time immutability guarantees without runtime overhead:

- **Construction:** use `ImmutableCollectionsMarshal.AsImmutableArray(array)`
  for zero-copy wrapping of a freshly-built `T[]` (caller gives up ownership).
- **Span access:** `immutableArray.AsSpan()` is JIT-inlined to identical
  codegen as raw `T[]` — the `ReadOnlySpan<T>` constructor is `[Intrinsic]`.
- **Sharing:** expose `public ImmutableArray<T> FooImmutable => _foo;`
  properties for zero-copy sharing between instances.
- **Interop with `T[]` APIs:** use `ImmutableCollectionsMarshal.AsArray()`
  to unwrap (zero-copy). Only do this when the consumer provably never
  mutates the array.

Do **not** use `internal T[] FooArray` accessors to expose backing arrays —
that relies on convention instead of the type system.

### Allocation-sensitive hot paths — use `ArrayPool<T>`

For scratch buffers in per-glyph pipelines (variation, hinting, rasterizer):

- Rent from `ArrayPool<T>.Shared.Rent(size)` in a `try` block.
- Return in the matching `finally` block.
- Always `Array.Clear(rented, 0, usedLength)` before use — rented arrays
  may contain stale data.
- Do not pool output arrays that escape the method (e.g. the `short[]`
  arrays that become part of a new `Outline`).

### SIMD — use `Vector<T>` for portable auto-scaling

Use `Vector<float>` / `Vector<int>` from `System.Numerics` for SIMD hot
paths. It auto-sizes to the best available width at runtime:

- **128-bit** on ARM64 (AdvSIMD) and x64 without AVX2
- **256-bit** on x64 with AVX2
- **512-bit** on x64 with AVX-512

Pattern:

- Guard with `Vector.IsHardwareAccelerated`.
- Use `Vector<T>.Count` for the lane count (not hardcoded).
- Main loop: `for (; i + Vector<float>.Count <= n; i += Vector<float>.Count)`.
- Scalar tail: `for (; i < n; i++)`.

Do **not** hardcode `Vector128<float>` — that leaves performance on the
table on AVX2/AVX-512 machines. Do **not** use platform-specific types
like `Sse2` or `AdvSimd` — `Vector<T>` handles the dispatch.
