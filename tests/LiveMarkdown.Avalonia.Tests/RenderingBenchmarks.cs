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
        string renderLabel = "render (node sync)")
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
            AppendRow("layout (measure + arrange)", samples.Select(s => s.LayoutMs));
            if (samples.Any(s => s.LayoutMs > 0)) AppendRow("render + layout", samples.Select(s => s.OwnedMs));
            builder.AppendLine();
            builder.Append("steps: ").Append(samples.Count)
                .Append(", final length: ").Append(finalLength.ToString("N0"))
                .Append(" chars, allocated: ").Append((allocatedBytes / (1024d * 1024d)).ToString("F1")).AppendLine(" MB");
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

            var renderMs = Time(() => Renderer.DocumentUpdate = update);
            var layoutMs = Time(() =>
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
            });

            allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            previous = update;
            samples.Add(new PhaseSample(parseMs, renderMs, layoutMs));
        }

        public void ResetSamples()
        {
            samples.Clear();
            allocated = 0;
        }

        public void AddSample(PhaseSample sample) => samples.Add(sample);

        public void AddAllocated(long bytes) => allocated += bytes;

        public BenchmarkReport Report(string? renderLabel = null) =>
            renderLabel is null ?
                new BenchmarkReport(title, samples, allocated, source.Length) :
                new BenchmarkReport(title, samples, allocated, source.Length, renderLabel);

        public void Dispose() => Window.Close();
    }
}
