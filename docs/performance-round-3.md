# LiveMarkdown performance round 3

Baseline: merged PR #4, `cc7f78bafe66320161c69133daf658632cf92c21`.
Branch: `codex/livemarkdown-perf-round-3`; worktree: `perf-round-3-codex`.
Date: 2026-11-07. All eight findings are implemented.

| ID | Status | Implementation | Deterministic regressions |
| --- | --- | --- | --- |
| R3-01 | Done | Producer-owned splice proof skips retained built-in prefixes only for matching predecessor/factory state; manual deltas and custom-node barriers stay conservative. Definition fallbacks publish full renders. | `RoundThreeStreamingTests`: counts, identity/spans, custom dirtiness, factories, reordered/foreign/manual updates, fresh-render parity |
| R3-02 | Done | `CodeBlockNode` caches source positions, uses an append boundary or bounded search, and normalizes positions from parsed tails. | `RoundThreeStreamingTests`: 32/512-line counts, unterminated edits, spliced fences, blank lines, truncation, indentation, cancellation |
| R3-03 | Done | Projection reuse requires matching version, ordered block identities, spans and complete text identities. Clear/rebind releases the snapshot. | `RoundThreeLayoutTests`: no rebuild/notification on no-op; mutable buffers, embedded text, visibility/order, spans, versions |
| R3-04 | Done | `MarkdownListGrid` builds row offsets during measurement, reuses them during arrange, and observes/removes placement handlers. | `RoundThreeLayoutTests`: sparse order, repeated arrange, spacing/placement, removal/clear |
| R3-05 | Done | Disk mutations update byte accounting; scheduled/config-change reconciliation replaces per-write directory scans. | `RoundThreeDiskCacheTests`: scan counts, concurrent cap, replace/remove/no-store, injected-clock LRU, external writes, config/clear, old streaming transaction |
| R3-06 | Done | Waiter-aware reservations retire entry locks only after the last holder/waiter leaves. | `RoundThreeDiskCacheTests`: cancellation, queued holders, failure cleanup, zero retained locks |
| R3-07 | Done | `ImageLoadCoordinator` shares cold bytes/cache fill by URI + handler/cache identity; consumers retain separate streams, decodes and cancellation. Local/resource/raw/data handlers decode directly. | `RoundThreeImageLoadTests`: fetch/cache counts, bytes/images, peer/all cancellation, retry, scope, fresh/stale cache paths, original FileStream |
| R3-08 | Done | Owned undisplayed decoded results are disposed exactly once. Built-in bitmaps opt in; custom decoders default to borrowed results. | `RoundThreeImageLoadTests`: owned/borrowed stale completion, cache failure, ownership transfer |

## Validation

- **214 focused tests passed**, including all **45 new deterministic cases**: 16 streaming,
  6 projection/list, 11 disk/lock, 12 image load/ownership.
- Existing producer tests now await dispatcher notifications; the disk LRU test uses an injected
  clock. The touched tests add no sleeps or elapsed-time verdicts.
- `PerformanceRoundThreeProbes.cs` remains an explicit audit. The fixed image probe waits for
  consumer joins rather than two duplicate fetches. Product and test failures encountered during
  implementation, and their corrections, are retained in the logs.
- Cross-reviews checked projection/list lifetime and streaming proof/offset correctness.
- Release .NET 8 build and all three updated explicit audit cases passed. Five deterministic
  benchmark-comparator tests passed; the seven-workload suite passed on both revisions twice.
- No full local suite was run. Focused results: `focused-regressions.log` in the artifact directory below.

## Benchmarks

`RoundThreeBenchmarks.cs` has seven fixed workloads, two warm-ups and seven samples each.
The **same harness and headless-host bytes** ran against the untouched merged baseline in
a separate worktree and against the fixes, in two sequential before/after pairs.

| Fixed workload | Before | After |
| --- | --- | --- |
| 16 producer tail updates, 256 paragraphs | 4,080 unchanged-node visits | 0 visits |
| 16 producer code appends, 256-line prefix | Full line scan in baseline source; counter unavailable | 16 inspected lines, no position searches |
| 16 unchanged projection refreshes, 200 paragraphs | 16 builds; 199,168 allocated bytes | 0 builds; 94,592 bytes (**52.5% less**) |
| 16 list arrangements, 128 sparse rows | 651,008 allocated bytes | 478,848 bytes (**26.4% less**); no row-map builds |
| 16 disk writes after 32 seeded entries | ~57.6 MB process allocation; 48 retained locks | ~1.44 MB (**97.5% less**); 0 measured scans/locks |
| 8 simultaneous cold image consumers | 8 fetches, 8 decodes | 1 fetch, 8 decodes |
| 1 canceled image whose owned decode completes late | 0 disposals | Exactly 1 disposal |

Streaming timings/allocations cover only UI renderer assignment inside the producer subscriber;
parsing is excluded. Projection/list allocations use the current UI thread. Disk/image allocation
is **process-wide**, including unrelated test-host work. Missing historical counters remain null.

Timings are observations, never gates. Paragraph rendering decreased in both pairs; disk writes
decreased from ~67–68 ms to ~8–25 ms. Code append and list/projection timings were mixed, so no
timing gain is claimed there. Code append allocation increased 0.5% (2,072 bytes from growing the
cached position array). Coalescing/ownership adds bookkeeping: zero-latency image mocks can take
longer; stale cleanup allocated 6,392 bytes versus 4,752. Image gains are fewer fetches and correct
resource release, not a claimed speedup for a zero-latency mock.

This suite exercises producer/node work, opted-in projections and disk/image lifetimes missing
from the original six workloads. The original rendering suite is still available unchanged.

```sh
python3 tools/rendering-benchmark.py run --suite round3 --output /tmp/lm-round3-after.json --msbuild-property MermaiderVersion=0.12.2-avalloy.10
python3 tools/rendering-benchmark.py compare /tmp/lm-round3-before.json /tmp/lm-round3-after.json
```

Copy the unchanged `RoundThreeBenchmarks.cs` into an isolated baseline worktree and pass that
path with `--repo`. The runner preserves logs and rejects mismatched harness/host sources,
parameters, corpora, configuration and environment. Existing outputs are never overwritten.

These measurements were taken before committing: the baseline report's dirty marker is the
copied benchmark fixture alone, while the optimized report's marker includes this round's fixes.
The isolated baseline's product sources match `cc7f78ba`; both reports use byte-identical
benchmark and headless-host sources. Raw reports are preserved without rewriting their provenance.

## Compatibility boundaries

- External disk changes reconcile at `CleanupInterval`; local mutations enforce the cap
  immediately. Nonpositive intervals request reconciliation every mutation; changed settings
  force a scan. Instantaneous knowledge of arbitrary filesystem writers is not promised.
- HTTP/custom handlers coalesce by default; `CoalesceConcurrentLoads` opts out. Built-in local,
  resource, raw and data handlers opt out to preserve their original seekable streams.
- Decoder streams can be read-only and remain loader-owned. `OwnsDecodedImage` opts into
  undisplayed-result cleanup. Displayed results transfer to the consumer.
- Prefix proofs cover producer-owned retained built-ins. Public/manual-delta audit paths
  deliberately still inspect the unchanged prefix; custom nodes retain conservative behavior.
- Heading-definition fallback matches fresh rendering. The pre-existing dynamic heading-URL
  rendering gap is a separate follow-up, not changed by this optimization.

Artifacts (four raw benchmark reports, complete logs and `benchmark-comparison.txt`):
`/Users/john/.codex/diagnostics/2026-11-07/livemarkdown-perf-round3/`.
