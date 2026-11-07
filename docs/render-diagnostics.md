# Synchronous rendering diagnostics

Subscribe to the standard .NET `DiagnosticListener` named `LiveMarkdown.Render` to inspect document
application costs. Enable `DocumentUpdate` for every changed document, or `SlowDocumentUpdate` for
only applications taking at least 2 ms. With neither event enabled, the renderer creates no per-update
metrics accumulator and takes no diagnostic phase timestamps. Enabling both emits both event names
for a slow application; consumers should choose one stream to avoid double counting.

The payload is a `MarkdownRenderReport`, containing scalar metadata and counters only. Its text form
is also suitable for forwarding into an application's bounded diagnostic log. No source text,
syntax tree, renderer, or controls are carried in the report.

`Kind` is `full`, `incremental`, or `clear`. Incremental mode requires an update following the renderer's
actual previous update. Assigning the identical update object again is a cache hit and emits no report.
`Completed=false` reports an update that unwound before completion.

Phase durations are exclusive: block updates, inline updates, code-block updates, node construction,
proxy collection/tree mutations, synchronous code styling, and other work sum to `Total`. In particular,
parent block times exclude time attributed to nested inline updates and attachment. Node update counts
include attempts, and `SkippedNodes` counts unaffected nodes. Tree mutation counts are operations, not
the number of descendants attached or detached.

This span starts after the renderer accepts a changed `DocumentUpdate`. It excludes parsing, later
layout/paint, asynchronous diagram rendering, and report dispatch/formatting. Synchronous nested
renderers contribute to the enclosing report instead of emitting a second overlapping report.

Tests use a manually advanced `TimeProvider` to verify exclusive accounting. Rendering regressions
assert structure, source identity, and counts, not wall-clock thresholds.
