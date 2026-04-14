# CLAUDE.md

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

### SIMD — use `Vector128<float>` with scalar tail

The SDF rasterizer uses `Vector128<float>` (4 lanes) for the inner edge
loop. Pattern:

- Check `Vector128.IsHardwareAccelerated` once at the top.
- Main loop: `for (; i + 3 < n; i += 4)` with `Vector128.LoadUnsafe`.
- Scalar tail: `for (; i < n; i++)` for remaining elements.
- Use `ref` + `Unsafe.Add` for span-based array access in the SIMD path.

This works on both ARM64 (AdvSIMD) and x64 (SSE2+).
