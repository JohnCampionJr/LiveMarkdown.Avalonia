using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// Wall-clock benchmarks for the streaming hot paths. These are NOT correctness tests: they print a
/// table and always pass. Run them on purpose, before and after a change, and compare the tables:
///
/// <code>
/// dotnet test tests/LiveMarkdown.Avalonia.Tests --filter Category=Benchmark
/// </code>
///
/// <para>Each streaming scenario appends to a document one step at a time, exactly as a host streaming
/// model output would, and times three phases separately per append: <b>parse</b> (Markdig, not ours),
/// <b>render</b> (assigning <see cref="MarkdownRenderer.DocumentUpdate"/>, which syncs the node tree),
/// and <b>layout</b> (<see cref="Layoutable.UpdateLayout"/>, which runs measure, text shaping, and
/// arrange). Parse is reported so it can be subtracted; render and layout are what this library owns.</para>
///
/// <para>Step counts are deliberately small so the whole fixture runs in a couple of minutes; the code block
/// and search scenarios grow super-linearly, so raise their constants to see the shape of the curve.</para>
///
/// <para>Numbers are from the Debug test build under the headless platform, so they are only comparable
/// with each other on the same machine. Set <c>LIVEMARKDOWN_BENCHMARK_RESULTS</c> to a file path to
/// append each table there for a side-by-side diff.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
[Explicit("Performance benchmark. Run with: dotnet test --filter Category=Benchmark")]
[Category("Benchmark")]
public class RenderingBenchmarks
{
    private const string ResultsPathVariable = "LIVEMARKDOWN_BENCHMARK_RESULTS";

    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession()
    {
        session = HeadlessSession.Current;
    }

    /// <summary>
    /// Finding 1 and 5: a fenced code block receiving one line per append. Every append re-lays-out the
    /// whole block through <c>MarkdownInlinesTextSource</c> (two runs per line) and re-enters syntax
    /// highlighting several times.
    /// </summary>
    [Test]
    public async Task StreamingCodeBlock()
    {
        const int lines = 200;

        var report = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness("Streaming code block (csharp, 1 line per append)");
                harness.Append("```csharp\n");
                for (var i = 0; i < lines; i++)
                {
                    harness.Append(CodeLine(i));
                }

                return harness.Report();
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>
    /// Finding 2: a paragraph with many inline code chips receiving a few words per append. Every
    /// layout rebuilds the paint snapshot, which shapes a nested layout for every chip.
    /// </summary>
    [Test]
    public async Task StreamingParagraphWithCodeChips()
    {
        const int chips = 40;
        const int appends = 150;

        var report = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness($"Streaming paragraph with {chips} code chips (3 words per append)");
                var opening = new StringBuilder();
                for (var i = 0; i < chips; i++)
                {
                    opening.Append("Call `Method").Append(i).Append("(arg)` then read `field_").Append(i).Append("`. ");
                }

                harness.Append(opening.ToString());
                for (var i = 0; i < appends; i++)
                {
                    harness.Append($"more streamed text {i} ");
                }

                return harness.Report();
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>
    /// Finding 4: a foreground search highlight is active while paragraphs stream in. Every append
    /// tears the highlight down from every matched block and reapplies it after layout.
    /// </summary>
    [Test]
    public async Task StreamingParagraphsWithActiveSearch()
    {
        const int paragraphs = 120;

        var report = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness($"Streaming {paragraphs} paragraphs with an active foreground search");
                var styles = new TextHighlightStyles();
                styles.Set(
                    MarkdownRenderer.DefaultTextSearchHighlightName,
                    new TextHighlightStyle { Background = Brushes.Yellow, Foreground = Brushes.Red });
                MarkdownTextBlock.SetHighlightStyles(harness.Renderer, styles);
                harness.Renderer.ApplyTextSearch("item");

                for (var i = 0; i < paragraphs; i++)
                {
                    harness.Append(Paragraph(i) + "\n\n");
                }

                return harness.Report();
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>
    /// What an active search costs AS THE DOCUMENT GROWS — the shape, not a single number.
    /// </summary>
    /// <remarks>
    /// <para><see cref="StreamingParagraphsWithActiveSearch"/> measures a fixed 120 paragraphs, so it
    /// reports a cost without saying what that cost is a function of. This runs the same work in
    /// stages, with and without the search, because the interesting quantity is the DIFFERENCE per
    /// stage: the matcher runs over every block in the renderer on every completed layout, so if the
    /// gap between the two columns grows stage over stage then the search is O(document) per append
    /// and a long transcript pays for a query it already answered.</para>
    ///
    /// <para>Stages rather than an average for the same reason the growing-paragraph scenario uses
    /// them: an average over a run that starts empty and ends long hides exactly the growth being
    /// looked for.</para>
    /// </remarks>
    [Test]
    public async Task SearchWhileTheDocumentGrows()
    {
        const int stages = 4;
        const int perStage = 100;

        var reports = await session.Dispatch(
            () =>
            {
                var collected = new List<BenchmarkReport>();

                // Three configurations, because the two searches are opposite ends of the same feature.
                // "item" is in every paragraph the fixture writes, so it matches EVERY block twice — the
                // worst case, where the result set itself is O(document) and no caching can shrink it.
                // The rare query matches nothing, which is what a real search mostly does: a person hunts
                // for one error message in a long transcript, and the blocks that do not contain it are
                // the overwhelming majority. A fix that only helps the worst case would be measuring a
                // scenario nobody has.
                foreach (var (label, query) in new (string, string?)[]
                {
                    ("no search", null),
                    ("search matching every block", "item"),
                    ("search matching nothing", "xyzzy-no-such-token"),
                })
                {
                    using var harness = new StreamingHarness(label);

                    if (query is not null)
                    {
                        var styles = new TextHighlightStyles();
                        styles.Set(
                            MarkdownRenderer.DefaultTextSearchHighlightName,
                            new TextHighlightStyle { Background = Brushes.Yellow, Foreground = Brushes.Red });
                        MarkdownTextBlock.SetHighlightStyles(harness.Renderer, styles);
                        harness.Renderer.ApplyTextSearch(query);
                    }

                    for (var stage = 0; stage < stages; stage++)
                    {
                        harness.ResetSamples();
                        for (var i = 0; i < perStage; i++)
                        {
                            harness.Append(Paragraph(stage * perStage + i) + "\n\n");
                        }

                        collected.Add(harness.ReportStage(
                            $"{label} — appends {stage * perStage + 1}-{(stage + 1) * perStage} "
                            + $"(document is {(stage + 1) * perStage} paragraphs)"));
                    }
                }

                return collected;
            },
            CancellationToken.None);

        foreach (var report in reports) Publish(report);
    }

    /// <summary>
    /// Baseline for the other streaming scenarios: plain paragraphs, no search, no chips.
    /// </summary>
    [Test]
    public async Task StreamingParagraphs()
    {
        const int paragraphs = 120;

        var report = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness($"Streaming {paragraphs} plain paragraphs");
                for (var i = 0; i < paragraphs; i++)
                {
                    harness.Append(Paragraph(i) + "\n\n");
                }

                return harness.Report();
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>
    /// A paragraph written a sentence at a time — the shape prose actually streams in.
    /// </summary>
    /// <remarks>
    /// <para>Every other streaming scenario here appends a WHOLE paragraph, so each append creates a
    /// new block and the block it lands in is always empty. That is the easy half of streaming. An
    /// agent writing prose does the other half: sentence after sentence into the SAME paragraph,
    /// which re-lays-out entirely each time.</para>
    ///
    /// <para>The hole this closes was found from the Tweed side, where a token into a growing
    /// paragraph measured 1.9 ms rising to 7.0 while the same token starting a new paragraph stayed
    /// flat at 0.8. Three reports, over the same run, so the growth is visible rather than averaged
    /// away: what an append costs while the paragraph is short, middling and long.</para>
    /// </remarks>
    [Test]
    public async Task StreamingAGrowingParagraph()
    {
        const int sentencesPerStage = 40;

        var reports = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness("Streaming a growing paragraph");
                var written = 0;
                var stages = new List<BenchmarkReport>();

                foreach (var stage in new[] { "short", "middling", "long" })
                {
                    harness.ResetSamples();
                    var layoutsBefore = MarkdownTextBlock.TextLayoutsCreated;
                    for (var i = 0; i < sentencesPerStage; i++)
                    {
                        harness.Append(Sentence(written++));
                    }

                    var layouts = (MarkdownTextBlock.TextLayoutsCreated - layoutsBefore) / (double)sentencesPerStage;
                    stages.Add(harness.ReportStage(
                        $"Streaming a sentence into a {stage} paragraph ({layouts:F1} text layouts per append)"));
                }

                return stages;
            },
            CancellationToken.None);

        foreach (var report in reports) Publish(report);
    }

    /// <summary>One sentence of prose, with the inline formatting a reply carries, and a space to join.</summary>
    private static string Sentence(int i) =>
        $"Sentence {i} of the reply, with **bold** and `code` in it, long enough to matter. ";

    /// <summary>
    /// Phase 4b: appending to a document that is ALREADY long, through the real producer.
    /// </summary>
    /// <remarks>
    /// Every other scenario here drives the renderer directly and parses on the calling thread, which
    /// is fine for measuring what rendering costs but says nothing about the producer. This one goes
    /// through <see cref="ObservableStringBuilder"/> and <see cref="MarkdownUpdateProducer"/> as a host
    /// does, so the parse runs where it really runs — off the UI thread — and what is measured is what
    /// the UI thread actually pays, plus the wall-clock wait from append to rendered.
    ///
    /// <para>It exists because the suite's other documents top out around 20,000 characters, and the
    /// cost this measures does not appear until well past that: a transcript reaches a quarter of a
    /// megabyte, and re-parsing the whole of it on every token is invisible at 20 KB.</para>
    /// </remarks>
    [Test]
    public async Task StreamingIntoALongDocument()
    {
        const int seedBytes = 250 * 1024;
        const int appends = 30;

        var report = await session.Dispatch(
            () =>
            {
                var builder = new ObservableStringBuilder();
                var renderer = new MarkdownRenderer { MarkdownBuilder = builder };
                var window = CreateWindow(renderer);
                try
                {
                    window.Show();
                    window.UpdateLayout();

                    var seed = new StringBuilder();
                    var next = 0;
                    while (seed.Length < seedBytes) seed.Append(Paragraph(next++)).Append("\n\n");
                    builder.Append(seed.ToString());
                    Settle(window, builder, renderer);

                    var samples = new List<PhaseSample>();
                    long allocated = 0;
                    for (var i = 0; i < appends; i++)
                    {
                        var text = Paragraph(next++) + "\n\n";
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        var append = Time(() => builder.Append(text));
                        var settle = Time(() => Settle(window, builder, renderer));
                        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                        samples.Add(new PhaseSample(0, append, settle));
                    }

                    return new BenchmarkReport(
                        $"Appending to a {builder.Length / 1024} KB document through the real producer",
                        samples,
                        allocated,
                        builder.Length,
                        "Append() on the UI thread",
                        "append to rendered (parse runs off-thread)");
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>Pumps until the renderer has taken the builder's latest version.</summary>
    private static void Settle(Window window, ObservableStringBuilder builder, MarkdownRenderer renderer)
    {
        var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (renderer.DocumentUpdate is { } update && update.Version >= builder.Version) return;
            Thread.Sleep(0);
        }

        throw new TimeoutException("the producer never caught up with the builder");
    }

    /// <summary>
    /// Finding 3: a large mixed document rendered from nothing. Every block and inline goes through
    /// node factory resolution.
    /// </summary>
    [Test]
    public async Task FullRenderMixedDocument()
    {
        const int repetitions = 3;
        var markdown = MixedDocument(sections: 60);
        var document = Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);

        var report = await session.Dispatch(
            () =>
            {
                var samples = new List<PhaseSample>();
                long allocated = 0;
                for (var i = 0; i < repetitions; i++)
                {
                    var renderer = new MarkdownRenderer();
                    var window = CreateWindow(renderer);
                    try
                    {
                        window.Show();
                        window.UpdateLayout();

                        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        var render = Time(() => renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(document));
                        var layout = Time(window.UpdateLayout);
                        allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                        samples.Add(new PhaseSample(0, render, layout));
                    }
                    finally
                    {
                        window.Close();
                    }
                }

                return new BenchmarkReport(
                    $"Full render of a mixed document ({markdown.Length:N0} chars, best of {repetitions})",
                    samples,
                    allocated / repetitions,
                    markdown.Length);
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>
    /// Finding 7: dragging a selection across a document with many blocks. Every pointer move
    /// recomputes the selection range of every block in the scope.
    /// </summary>
    [Test]
    public async Task DragSelectionAcrossManyBlocks()
    {
        const int paragraphs = 300;
        const int moves = 120;

        var report = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness($"Drag selection across {paragraphs} paragraphs ({moves} pointer moves)");
                var text = new StringBuilder();
                for (var i = 0; i < paragraphs; i++)
                {
                    text.Append(Paragraph(i)).Append("\n\n");
                }

                harness.Append(text.ToString());
                harness.ResetSamples();

                var window = harness.Window;
                var start = new Point(20, 10);
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);

                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 1; i <= moves; i++)
                {
                    var point = new Point(20 + i % 5 * 40, 10 + i * 4);
                    var elapsed = Time(() => window.MouseMove(point, RawInputModifiers.LeftMouseButton));
                    harness.AddSample(new PhaseSample(0, elapsed, 0));
                }

                harness.AddAllocated(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                window.MouseUp(new Point(20, 10 + moves * 4), MouseButton.Left);

                return harness.Report(renderLabel: "pointer move");
            },
            CancellationToken.None);

        Publish(report);
    }

    /// <summary>
    /// Finding 6: a left press while a large selection exists. The press handler materializes the
    /// selected text of every block to decide whether anything is selected.
    /// </summary>
    [Test]
    public async Task PressWithLargeSelection()
    {
        const int paragraphs = 300;
        const int presses = 10;

        var report = await session.Dispatch(
            () =>
            {
                using var harness = new StreamingHarness($"Left press with all of {paragraphs} paragraphs selected ({presses} presses)");
                var text = new StringBuilder();
                for (var i = 0; i < paragraphs; i++)
                {
                    text.Append(Paragraph(i)).Append("\n\n");
                }

                harness.Append(text.ToString());
                harness.ResetSamples();

                var window = harness.Window;

                // Alternating between two points that are far apart, because pressing the SAME point
                // repeatedly is a multi-click run: Avalonia counts the second, third and fourth, and
                // this renderer answers those with select-word, select-section and select-ALL. That
                // measured a quadruple click selecting the whole document rather than a press.
                var points = new[] { new Point(30, 40), new Point(500, 300) };
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < presses; i++)
                {
                    var point = points[i % points.Length];
                    harness.Renderer.SelectAll();
                    var elapsed = Time(() => window.MouseDown(point, MouseButton.Left));
                    window.MouseUp(point, MouseButton.Left);
                    harness.AddSample(new PhaseSample(0, elapsed, 0));
                }

                harness.AddAllocated(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

                return harness.Report(renderLabel: "left press");
            },
            CancellationToken.None);

        Publish(report);
    }

    private static string CodeLine(int i) =>
        $"    var value{i} = Compute({i}, \"text {i}\") + items[{i % 7}].Name; // trailing comment {i}\n";

    private static string Paragraph(int i) =>
        $"Paragraph {i} has **bold item text**, *emphasis*, a [link {i}](https://example.com/{i}), " +
        $"and `inline{i}` code so that the item wraps across a few lines in the window.";

    private static string MixedDocument(int sections)
    {
        var builder = new StringBuilder();
        for (var section = 0; section < sections; section++)
        {
            builder.Append("## Section ").Append(section).Append("\n\n");
            for (var p = 0; p < 3; p++)
            {
                builder.Append(Paragraph(section * 3 + p)).Append("\n\n");
            }

            builder.Append("- first item with `code`\n- second item with **bold**\n- third item with a [link](https://example.com)\n\n");
            builder.Append("> A quote with *emphasis* in section ").Append(section).Append("\n\n");

            if (section % 5 == 0)
            {
                builder.Append("| Name | Value | Notes |\n|---|---:|---|\n");
                for (var row = 0; row < 4; row++)
                {
                    builder.Append("| row ").Append(row).Append(" | ").Append(row * 10).Append(" | `cell` text |\n");
                }

                builder.Append('\n');
            }

            if (section % 4 == 0)
            {
                builder.Append("```csharp\n");
                for (var line = 0; line < 12; line++)
                {
                    builder.Append(CodeLine(line));
                }

                builder.Append("```\n\n");
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// What one message costs to put on screen from nothing. This is not the streaming path — it is
    /// the SCROLL path: a host that virtualizes its transcript realizes a row as it comes into view,
    /// and pays this for it, on the UI thread, inside a frame. Every other scenario here measures
    /// what an already-realized document costs to change.
    /// </summary>
    [Test]
    public async Task RealizingAMessageFromCold()
    {
        foreach (var size in new[] { 1000, 4000, 16000 })
        {
            var markdown = AgentMessage(size);
            var report = await session.Dispatch(
                () =>
                {
                    // Two throwaway passes: the first realization of anything in a process pays for
                    // the font manager, the grammar registry and every static this library caches.
                    for (var warm = 0; warm < 2; warm++) Realize(markdown);

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    const int repetitions = 6;
                    var samples = new List<PhaseSample>();
                    long allocated = 0;
                    for (var i = 0; i < repetitions; i++)
                    {
                        var before = GC.GetAllocatedBytesForCurrentThread();
                        samples.Add(Realize(markdown));
                        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    }

                    return new BenchmarkReport(
                        $"Realizing a {markdown.Length / 1024} KB message from cold",
                        samples,
                        allocated,
                        markdown.Length,
                        "render (node sync)",
                        "layout (measure + arrange)");
                },
                CancellationToken.None);

            Publish(report);
        }
    }

    /// <summary>Builds and lays out one message in a fresh renderer, timing each phase.</summary>
    private static PhaseSample Realize(string markdown)
    {
        Markdig.Syntax.MarkdownDocument document = null!;
        var parse = Time(() => document = Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline));

        var renderer = new MarkdownRenderer();
        var window = CreateWindow(renderer);
        try
        {
            // The window is shown and laid out empty first, so its own construction is not in either
            // phase below — a host realizing a row already has one.
            window.Show();
            window.UpdateLayout();

            var render = Time(() => renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(document));
            var layout = Time(() =>
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            });

            return new PhaseSample(parse, render, layout);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Prose with the formatting an agent reply carries, and a fence every fifth block.</summary>
    private static string AgentMessage(int approximateLength)
    {
        var builder = new StringBuilder();
        var i = 0;
        while (builder.Length < approximateLength)
        {
            builder.Append(i % 5 == 4
                ? $"```csharp\nvar value{i} = Compute({i});\n```\n\n"
                : $"Paragraph {i} of an agent reply, with **bold**, `code` and a "
                    + $"[link](https://example.com/{i}) in it, long enough to wrap across lines.\n\n");
            i++;
        }

        return builder.ToString();
    }

    private static Window CreateWindow(MarkdownRenderer renderer) => new()
    {
        Width = 800,
        Height = 600,
        Content = new ScrollViewer { Content = renderer },
    };

    private static double Time(Action action)
    {
        var start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static void Publish(BenchmarkReport report)
    {
        var table = report.ToMarkdown();
        TestContext.Out.WriteLine(table);
        TestContext.Progress.WriteLine(table);

        if (Environment.GetEnvironmentVariable(ResultsPathVariable) is { Length: > 0 } path)
        {
            File.AppendAllText(path, table + Environment.NewLine);
        }
    }

    private readonly record struct PhaseSample(double ParseMs, double RenderMs, double LayoutMs)
    {
        public double OwnedMs => RenderMs + LayoutMs;
    }

    private sealed class BenchmarkReport(
        string title,
        IReadOnlyList<PhaseSample> samples,
        long allocatedBytes,
        int finalLength,
        string renderLabel = "render (node sync)",
        string layoutLabel = "layout (measure + arrange)",
        long renderBytes = 0,
        long layoutBytes = 0)
    {
        public string ToMarkdown()
        {
            var builder = new StringBuilder();
            builder.Append("### ").AppendLine(title);
            builder.AppendLine();
            builder.AppendLine("| phase | total ms | mean ms | p50 ms | p95 ms | max ms |");
            builder.AppendLine("|---|---:|---:|---:|---:|---:|");
            AppendRow("parse (Markdig)", samples.Select(s => s.ParseMs));
            AppendRow(renderLabel, samples.Select(s => s.RenderMs));
            AppendRow(layoutLabel, samples.Select(s => s.LayoutMs));
            if (samples.Any(s => s.LayoutMs > 0)) AppendRow("render + layout", samples.Select(s => s.OwnedMs));
            builder.AppendLine();
            builder.Append("steps: ").Append(samples.Count)
                .Append(", final length: ").Append(finalLength.ToString("N0"))
                .Append(" chars, allocated: ").Append((allocatedBytes / (1024d * 1024d)).ToString("F1")).Append(" MB");

            // Which side of the fence the bytes are on is the whole question for anything that grows:
            // the node sync is ours and can be fixed, the layout is Avalonia's text pipeline.
            if (renderBytes > 0 || layoutBytes > 0)
            {
                builder
                    .Append(" (").Append(renderLabel).Append(' ').Append((renderBytes / (double)samples.Count / 1024d).ToString("F0"))
                    .Append(" KB, ").Append(layoutLabel).Append(' ').Append((layoutBytes / (double)samples.Count / 1024d).ToString("F0"))
                    .Append(" KB per step)");
            }

            builder.AppendLine();
            return builder.ToString();

            void AppendRow(string phase, IEnumerable<double> values)
            {
                var sorted = values.OrderBy(v => v).ToArray();
                if (sorted.Length == 0 || sorted[^1] == 0) return;

                builder.Append("| ").Append(phase)
                    .Append(" | ").Append(sorted.Sum().ToString("F1"))
                    .Append(" | ").Append(sorted.Average().ToString("F2"))
                    .Append(" | ").Append(Percentile(sorted, 0.5).ToString("F2"))
                    .Append(" | ").Append(Percentile(sorted, 0.95).ToString("F2"))
                    .Append(" | ").Append(sorted[^1].ToString("F2"))
                    .AppendLine(" |");
            }
        }

        private static double Percentile(double[] sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }

    /// <summary>
    /// Drives a renderer the way a streaming host does: grow the source, parse, hand the renderer an
    /// incremental update, and lay the window out. Parsing is synchronous here so the three phases can be
    /// timed independently of the producer's background scheduling.
    /// </summary>
    private sealed class StreamingHarness : IDisposable
    {
        private readonly string title;
        private readonly StringBuilder source = new();
        private readonly List<PhaseSample> samples = [];
        private MarkdownDocumentUpdate? previous;
        private long version;
        private long allocated;
        private long renderAllocated;
        private long layoutAllocated;

        public MarkdownRenderer Renderer { get; }

        public Window Window { get; }

        public StreamingHarness(string title)
        {
            this.title = title;
            Renderer = new MarkdownRenderer();
            Window = CreateWindow(Renderer);
            Window.Show();
            Window.UpdateLayout();
        }

        public void Append(string text)
        {
            var startIndex = source.Length;
            source.Append(text);
            var markdown = source.ToString();
            var change = new ObservableStringBuilderChangedEventArgs(startIndex, text.Length, markdown.Length, ++version);

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            var parseStart = Stopwatch.GetTimestamp();
            var document = Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline);
            var parseMs = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;

            MarkdownDocumentUpdate update = previous is null ?
                new MarkdownDocumentUpdate.Full(document, change.Version) :
                new MarkdownDocumentUpdate.Incremental(previous, document, change);

            var beforeRender = GC.GetAllocatedBytesForCurrentThread();
            var renderMs = Time(() => Renderer.DocumentUpdate = update);
            var beforeLayout = GC.GetAllocatedBytesForCurrentThread();
            var layoutMs = Time(() =>
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
            });

            renderAllocated += beforeLayout - beforeRender;
            layoutAllocated += GC.GetAllocatedBytesForCurrentThread() - beforeLayout;

            allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            previous = update;
            samples.Add(new PhaseSample(parseMs, renderMs, layoutMs));
        }

        public void ResetSamples()
        {
            samples.Clear();
            allocated = 0;
            renderAllocated = 0;
            layoutAllocated = 0;
        }

        public void AddSample(PhaseSample sample) => samples.Add(sample);

        public void AddAllocated(long bytes) => allocated += bytes;

        /// <summary>A report of the samples taken so far under a title of its own, for a run
        /// measured in stages rather than as one average.</summary>
        public BenchmarkReport ReportStage(string stageTitle) =>
            new(
                stageTitle,
                [.. samples],
                allocated,
                source.Length,
                renderBytes: renderAllocated,
                layoutBytes: layoutAllocated);

        public BenchmarkReport Report(string? renderLabel = null) =>
            renderLabel is null ?
                new BenchmarkReport(title, samples, allocated, source.Length) :
                new BenchmarkReport(title, samples, allocated, source.Length, renderLabel);

        public void Dispose() => Window.Close();
    }
}
