# LiveMarkdown performance round 4

Date: 2026-11-07. Baseline: released `2.4.0-avalloy.12`, commit
`3acb29892c3dbffc45efcffeb564d9d7c74136d2` (merged #3, #4 and #5).
Branch: `codex/livemarkdown-perf-round-4`; worktree: `perf-round-4-codex`.

## Checklist

- [x] Start an isolated worktree from the published source.
- [x] Audit streaming allocations, layout and retention; preserve baseline probes.
- [x] Implement R4-01: parse only the changed suffix of eligible open fences.
- [x] Implement R4-02: unsubscribe detached renderers and replay on reattachment.
- [x] Implement R4-03: reuse transient measured layouts with explicit ownership.
- [x] Implement R4-04: reject guaranteed-uncacheable prewarms before tokenization.
- [x] Implement R4-05: avoid retaining unsupported language labels.
- [x] Implement R4-06: make read-only code inspection preserve incremental formatting.
- [x] Implement R4-07: reuse private block arrays and range-stable search aggregates.
- [x] Add deterministic regressions and run focused compatibility tests.
- [x] Compare identical audit harnesses and existing benchmark suites before/after.
- [x] Prepare the reviewable changes, checklist and validation notes.

## Findings and implementations

| ID | Original observation | Final implementation | Regression coverage |
| --- | --- | --- | --- |
| R4-01 | Eight small appends to a 4,096-line open fence allocated about 7.9 MB before rendering. Every update copied and parsed from the opening fence. | `OpenFenceAppend` uses Markdig to parse the original opening line plus only the last incomplete line/new suffix, then appends those parsed lines. Tracks each fragment's slice origin for renderer source coordinates. | Root/prose/heading prefixes, backticks/tildes, partial lines and fence closes, CRLF split across updates, source spans/render parity, clear, indentation and custom-pipeline fallback; bounded parsed-character count at 32/4,096 lines. |
| R4-02 | Eight detached renderers retained eight producer subscriptions and continued applying updates. | Release the subscription on visual detach; subscribe/replay on attach. Source replacement while detached stays unsubscribed. Initial off-tree rendering still works. | Shared/default producer counts, repeated attach cycles, source replacement, changes while paused, and a real in-flight parse held/released by explicit signals. |
| R4-03 | Resizing 17 code chunks constructed 51 layouts: measure at the new width, arrange at the old width, then arrange at the new width. | Transfer ownership of at most one spare layout across transient arrange passes; reuse only compatible constraints. Disposal tickets release spares without capturing the control. | Wrapped/unwrapped resize counts, content/font invalidation, cleanup after dispatcher work/detach, height limits/trimming parity and embedded-control fallback; existing raster/selection tests. |
| R4-04 | Two oversized prewarms each tokenized 1,024 lines despite neither result fitting the cache. | Reject warm-ups whose key/line minimum weight already exceeds the shared entry budget. Foreground formatting remains available. | Zero tokenization for repeated oversized warm-ups; foreground large-block text/formatting correctness. |
| R4-05 | 256 unknown language labels left 256 permanent dictionary entries. | Unknown labels use a stateless fallback without being inserted; supported labels normalize case and retain their strong grammar cache. | No dictionary growth for 256 unknown labels; fallback/case identity and existing syntax/theme tests. |
| R4-06 | Reading `CodeTextBlocks` made an unchanged 512-line highlight pass inspect every line forever. | View getters do not mark the source mutable; actual inline/content changes do. The collection is a read-only wrapper. Internal/deferred edits stay suppressed; removed chunks unsubscribe. | No-op read/append counts, exposed Run/collection edits, collection replacement/removal and existing deferred/highlighting tests. |
| R4-07 | Search rematched only the changed tail but rebuilt whole-document dictionaries/matches for every append. | Validate and reuse the private block array. If block order, ranges and owned highlights remain equal, update only private memo/snapshot state and retain the public aggregate. | Range-stable appends, added matches, external highlight replacement/removal, order/visibility, projection mutation and clear/rebind lifetime. |

`RoundFourPerformanceTests.cs` adds **19 deterministic cases**. An older document test that
expected detached views to keep updating now checks the new lifecycle through parser-entry,
release and completion signals. Its shared document-wait helper also awaits dispatcher
notifications instead of polling with `Task.Delay` and a five-second window.

## Measured results

The same explicit audit harness ran against isolated `.12` product sources and the fixes.
The unchanged rendering/round-three suites each used two warmups and seven samples per
scenario. No wall-clock or allocation thresholds gate tests; the normal regressions assert
work counts, identity, source/render parity and ownership/lifecycle outcomes.

| Workload | Before | After |
| --- | --- | --- |
| Eight appends, 4,096-line open fence, parsing only | 7,886,144 process-allocated bytes | 356,536 bytes (about **95.5% less**); 152 total parsed characters |
| Eight detached renderer subscriptions/updates | 8 / 8 | 0 / 0 |
| 17 code chunks resized, wrapped or unwrapped | 51 new layouts per resize | 34 |
| New chunk appended at a stable width | 2 new layouts; old 16 preserved | 1; old 16 still preserved |
| Two oversized 1,024-line prewarms | 1,024 + 1,024 tokenized lines | 0 + 0 |
| 256 unsupported language labels | 256 retained entries | 0 |
| Unchanged formatting after reading code views | 512 line visits | 0 |
| Eight searched appends, 256 paragraphs | 8 aggregate replacements; 1,097,352 process-allocated bytes | 0 replacements; 386,312 bytes (about **64.8% less**) |
| 16 unchanged search refreshes, 200 paragraphs | 94,208 UI-thread allocated bytes | 0 |
| 16 unchanged projection refreshes | 94,592 UI-thread allocated bytes | 384 |
| 16 document width changes, 200 paragraphs | 9,600 layouts; 29,538,424 UI-thread allocated bytes | 6,400; 25,233,864 bytes (**14.6% less**) |
| Fresh 200-paragraph view construction | 400 layouts; 13,834,056 UI-thread allocated bytes | 200; 13,492,960 bytes (**2.5% less**) |

The fence audit's manual `CaptureSnapshot(restartOffset)` still demonstrates the old API's
full-tail copy; the optimized producer no longer calls it for these appends. The new path's
remaining allocation includes one amortized growth of Markdig's line array. It parses only
the opening line/new suffix, rather than eliminating all retained source/AST storage.

Search/audit allocations are process-wide and include parsing, layout and test-host work;
they are not search-only or retained-heap measurements. Rendering/projection medians use
current UI-thread allocation. Native retained heap was not measured. Missing historical
counters remain null, never fabricated zero.

Timing observations include roughly 92 → 72 ms for the document-resize workload, but no
universal speedup is claimed. Code-tail rendering adds 272 bytes across 16 updates (under
0.1%); the large parse savings occur outside that renderer-only workload. Unchanged disk,
list and image work counts remain unchanged. Async image allocation/timing varied even
though that code was not changed, so no image improvement is attributed to this round.

## Compatibility and lifetime boundaries

- The fence shortcut accepts unindented built-in fences on the unmodified default pipeline,
  with a bounded opening line. Custom/configured pipelines, indentation, edits, closing fences
  and ambiguous CR/CRLF boundaries use normal full-context parsing. The last incomplete line
  must still be reparsed; a single enormous line is not made constant-cost.
- Only the current AST's source fragments and per-line origins are retained, not every historic
  partial-line revision. A cached block index avoids scanning the document prefix per append.
- Initial off-tree renderer observation remains supported. Once visually detached, the renderer
  pauses producer observation until attached again. Explicit `DocumentUpdate` assignments still
  render off-tree. Foxery's main `AgentMessageView` already clears `MarkdownBuilder` on detach;
  this fixes the library lifecycle for other consumers too.
- Layout reuse is deliberately transient: at most one spare, disposed on invalidation/detach
  and by a dispatcher disposal ticket. The callback does not retain the control. Avalonia owns
  the active layout; transfer prevents double disposal. Embedded/custom drawable runs use the
  native arrange path. Widths must match, and height equivalence must be proven.
- The layout bridge uses Avalonia's existing private `_textLayout` field alongside the existing
  `_textRunCache` bridge. Recheck it when upgrading Avalonia. Upstream ownership was inspected
  in [TextBlock](https://github.com/AvaloniaUI/Avalonia/blob/12.1.0/src/Avalonia.Controls/TextBlock.cs)
  and the reference-counted shaped-run implementation before adding this reuse.
- Prewarm admission detects guaranteed misses from minimum weight; it does not promise every
  admitted, unusually token-heavy block fits. Foreground rendering is never rejected.
- Raw mutable `CodeBlock.Inlines` exposure retains its conservative behavior. View inspection
  is cheap; actual edits opt into the comparison path.
- Search still validates order/visibility/registry identities. Structural or range changes
  rebuild aggregates; range-stable tail edits reuse them. Private block caches clear on full
  rebind, off-tree updates, detach and clear where no projection needs them.

## PR #6 review follow-up

- [x] Extract exactly one opening Markdown line for LF, CRLF or bare CR. Mixed CR-opener/LF-body
  inputs keep body lines and source coordinates identical to a full parse through partial
  appends and closing fences. The shortcut remains enabled for these valid inputs.
- [x] Separate actual content mutation notifications from logical attach/detach cache resets.
  Validate code chunks once on attachment, and before explicit highlighting while detached,
  because detached text blocks have released their nested collection subscriptions. Unchanged
  views retain authoritative line metadata; detected external edits use the conservative path.
- Six new deterministic cases cover both bugs and detached nested edits. The original behavior
  duplicated the first body line and visited 514 lines after reattachment instead of one.
  With the fixes, **74 focused tests passed**, including all **25 round-four regression cases**.
  The initial 196-test run below describes the PR's first revision.

## Validation and artifacts

- **196 focused tests passed**, including all 19 new cases, parser parity, raster equality,
  search/selection, wrapping, tables, syntax themes and renderer lifecycle.
- The **14 explicit audit cases passed on both revisions** with byte-identical probe sources.
- **13 existing benchmark workloads completed**, with unchanged harness/host sources.
- **Five comparator tests passed**; the Release **.NET 8 core build passed**.
- No full local suite was run while the app is in use.

Artifacts: `/Users/john/.codex/diagnostics/2026-11-07/livemarkdown-perf-round4/`.
Original baseline reports are `baseline-rendering-12.json` and `baseline-round3-12.json`;
optimized reports are `after-rendering.json` and `after-round3.json`. The two `*-comparison.txt`
reports preserve every counter and timing/allocation median. `comparable-before-clean/` and
`comparable-after-fixed/` contain the identical-harness audit reports, summarized in
`audit-comparison.txt`. Full test/build logs and original investigation failures are retained.

The baseline worktree has only the copied audit harness; its product tree matches `.12`.
The optimized benchmark revision is marked dirty because measurements preceded the commit.
The earlier comparison setup with a worktree mix-up did not compile and is not used. The
original width probes that did not drain platform resize jobs are also superseded. The final
probes assert actual window widths after draining real dispatcher notifications.

```sh
# Explicit before/after audit; use a fresh output directory.
env LM_ROUND4_OUTPUT_DIR=/tmp/lm-round4-probes dotnet test tests/LiveMarkdown.Avalonia.Tests/LiveMarkdown.Avalonia.Tests.csproj -c Release -p:MermaiderVersion=0.12.2-avalloy.10 --filter 'FullyQualifiedName~PerformanceRoundFourProbes' --logger 'console;verbosity=normal' > /tmp/lm-round4-probes.log 2>&1
```

The baseline audit asserts supported behavior bounds and reports counts; the normal regression
fixture asserts the optimized expectations. Existing benchmark commands/comparison rules are
in `rendering-benchmark.md`. Keep the same fixtures/host when comparing revisions.
