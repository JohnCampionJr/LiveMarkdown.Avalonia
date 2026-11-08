# Repeatable rendering benchmark

Run from a LiveMarkdown worktree with Python 3 and the repository's .NET SDK:

```sh
python3 tools/rendering-benchmark.py run --output /tmp/lm-before.json --msbuild-property MermaiderVersion=0.12.2-avalloy.10
# Apply the product change, keeping the benchmark and test host unchanged.
python3 tools/rendering-benchmark.py run --output /tmp/lm-after.json --msbuild-property MermaiderVersion=0.12.2-avalloy.10
python3 tools/rendering-benchmark.py compare /tmp/lm-before.json /tmp/lm-after.json
```

Use new output filenames for every run. The runner preserves the full test output beside
each JSON report and runs only this explicit fixture in Release, never the whole suite.
The Mermaider override selects this fork's published companion dependency; omit it when
the repository's default dependency is appropriate.

The six workloads have fixed text, ordering, sizes and operation counts:

| Workload | Measured work | Additional work counters |
| --- | --- | --- |
| Code append | 16 line appends to a highlighted 256-line block | Formatting line visits |
| Code unchanged | 16 unchanged tail-line assignments and highlights | Formatting line visits |
| Search refresh | 16 unchanged refreshes over 200 paragraphs | Matcher calls, match-list replacement, collections, text layouts |
| Table cell | 16 height changes to one cell of a four-cell table | Changed and unchanged cell measurements |
| Width change | 16 width changes between 420 and 800 over 200 paragraphs | Text layouts |
| Construction | Create, attach and lay out a fresh 200-paragraph renderer at 800 × 600 | Text layouts |

Every workload gets two discarded warm-ups and seven measured samples, using fresh
controls per sample. Global syntax/font caches stay warm. Setup, parsing, correctness
checks, JSON serialization and cleanup are excluded from measurements. Construction
intentionally includes window/scroll-host attachment. The shared headless host uses
Skia and bundled Inter fonts; it performs real shaping and layout, not GPU presentation.
No network, random input, sleeps, elapsed-time assertions or forced collections are used.

Determinism means identical work and input, **not identical elapsed times or allocations**.
The report preserves all samples and the comparison prints median time/allocation deltas
plus every distinct work-counter value. Correctness checks can fail; slower timings never
fail a test. Run on the same quiet machine without competing builds/agents for useful
timings, and repeat an A/B comparison before drawing conclusions from small changes.
Current-thread allocations cover synchronous UI work; they do not measure retained heap,
background allocations, process startup, cold caches, native memory or scrolling pixels.
The existing performance-audit probes and deterministic regression tests cover cache
budgets, concurrency and correctness separately.

Comparisons reject different machines, runtimes, architectures, Avalonia/Markdig versions,
build configurations, corpora, harness/test-host sources or workload parameters. Product
revision (including a dirty marker) is recorded but can differ. For an older revision,
copy the unchanged `RepeatableRenderingBenchmarks.cs` into its isolated worktree and
use this runner's `--repo /absolute/path/to/worktree` option. The test host and package
versions must also match. No product instrumentation patches are needed: counters absent
from historical source are reported as JSON `null` and displayed as unavailable, never zero.
Keep reports as CI/diagnostic artifacts rather than committing machine-specific numbers.

Comparator regression tests: `python3 -B -m unittest discover -s tools -p 'test_rendering_benchmark.py'`.

The round-three suite adds actual producer/node streaming, opted-in projections, sparse list
layout, disk-cache writes, concurrent image fetches and stale-result cleanup:

```sh
python3 tools/rendering-benchmark.py run --suite round3 --output /tmp/lm-round3.json --msbuild-property MermaiderVersion=0.12.2-avalloy.10
```

It uses `RoundThreeBenchmarks.cs` and the same comparison command. Streaming samples measure
UI renderer callbacks separately from parsing; async disk/image samples report process-wide
allocations. Its detailed workload definitions, results and tradeoffs are in
`performance-round-3.md`. The default six-workload suite remains unchanged.
