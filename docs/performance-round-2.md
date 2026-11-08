# LiveMarkdown performance round 2

Baseline: PR #3 including its review fixes, `422b54fee8764551c68de83cf7402eb1a9b06c55`.
Worktree: `perf-round-2-codex`; branch: `codex/livemarkdown-perf-round-2`.
PR #3 is merged into `codex/livemarkdown-avalloy-11-base`; PR #4 is rebased onto that branch.
The merged baseline has the same source tree as the recorded benchmark baseline above.

| Finding | Status | Location and verification |
| --- | --- | --- |
| R2-01: measure invalidation discards unchanged layout-text projections | Done | `MarkdownTextBlock`: invalidate projections for content events, preserve them for width/selection changes; verify nested edits and collection replacement |
| R2-02: every measure/arrange builds the same text-run offset index | Done | `MarkdownInlinesTextSource`: reuse index arrays when the run-length sequence is unchanged; verify width reuse and content invalidation |
| R2-03: code and dot span discovery repeats inline-tree walks | Done | `GetPaintSnapshot` and code hit testing: collect both span types once per content/decoration revision; keep public arrays independent |
| R2-04: code-chip pointer queries repeatedly allocate line-bound rectangles | Done | Cache read-only geometry for the current text layout only; verify wrapping, padding changes, replacement and detach |
| R2-05: shaped code chips retain every historical streaming revision | Done | Retain only keys used by the current paint snapshot; verify bounded history and unchanged-neighbor reuse |

Validation: 124 focused tests passed (including nine new deterministic cases, search,
selection, wrapping, table layout and raster equality). No full local suite was run.
Tests cover collection replacement/removal, edits while already measure-invalid, detached
reads, width and selection changes, immutable public results, and historical shape retirement.
Unsupported/unparented replacement span trees deliberately bypass caching.

The previous round's exact-source benchmark is retained as the baseline. The final
six-workload comparison reduced width-change allocations from 32,554,752 to 28,568,232
bytes; text-layout counts remain 9,600 because wrapping is still necessary. Search/no-op
work counts are unchanged. These are observations, never timing gates.

The public-API-only `InlineHitTestBenchmark` was run unchanged on both revisions: 40 code
chips, 500 pointer/rectangle queries per sample, seven samples. All 500 hits and returned
rectangle counts match. Median allocation fell from 31,240,000 bytes to zero; median
elapsed time was 65.69 ms versus 1.25 ms on this machine. First construction uses about
1.3% more allocations for cache bookkeeping, and code appends about 0.2% more; no
construction/append timing improvement is claimed.

Artifacts (full logs and JSON):
`/Users/john/.codex/diagnostics/2026-11-07/livemarkdown-perf-fixes/`.

Run the retained pointer probe explicitly:

```sh
env LM_INLINE_HIT_BENCHMARK_OUTPUT=/tmp/lm-pointer.json dotnet test tests/LiveMarkdown.Avalonia.Tests/LiveMarkdown.Avalonia.Tests.csproj -c Release -p:MermaiderVersion=0.12.2-avalloy.10 --filter 'FullyQualifiedName~InlineHitTestBenchmark.Repeated_pointer_queries' > /tmp/lm-pointer.log 2>&1
```

The six-workload benchmark remains documented in `docs/rendering-benchmark.md`.

Final checks: .NET 8 Release build passed; the nine new cache regressions passed again
after final lifetime cleanup; the final six-workload and pointer probes passed.
