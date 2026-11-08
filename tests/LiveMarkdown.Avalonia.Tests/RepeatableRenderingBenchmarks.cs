using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>Fixed work, warm caches, fresh controls per sample. Timings are observations, never gates.</summary>
[TestFixture, NonParallelizable, Explicit("Run with tools/rendering-benchmark.py run")]
public class RepeatableRenderingBenchmarks
{
    internal const int Steps = 16;
    internal const int Paragraphs = 200;
    internal const int CodeLines = 256;
    private const int Warmups = 2;
    private const int Samples = 7;
    internal static string Corpus => string.Join("\n\n", Enumerable.Range(0, Paragraphs)
        .Select(i => $"needle paragraph {i:D3}: fixed text with **bold**, *italic*, `inline code` and a [link](https://example.invalid)."));

    [Test]
    public async Task Fixed_rendering_workloads()
    {
        var results = await HeadlessSession.Current.Dispatch(() =>
        {
            var factories = new (string Id, Func<Trial> Create)[]
            {
                ("code-tail-append", () => Code(append: true)),
                ("code-unchanged-line", () => Code(append: false)),
                ("search-unchanged-refresh", Search),
                ("table-one-cell-change", Table),
                ("document-width-change", () => Document(resize: true)),
                ("document-view-construction", () => Document(resize: false)),
            };
            return factories.Select(scenario =>
            {
                // Warm-up includes grammar, JIT and font initialization. A new control tree is
                // still created for EVERY sample; global caches intentionally stay warm.
                for (var i = 0; i < Warmups; i++) Measure(scenario.Create);
                return new { id = scenario.Id, samples = Enumerable.Range(0, Samples)
                    .Select(_ => Measure(scenario.Create)).ToArray() };
            }).ToArray();
        }, CancellationToken.None);

        var report = new
        {
            schema = 1,
            workload = "rendering-v1",
            corpusSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Corpus))),
            harnessSha256 = Environment.GetEnvironmentVariable("LM_BENCHMARK_HARNESS_SHA256"),
            revision = Environment.GetEnvironmentVariable("LM_BENCHMARK_REVISION"),
            environment = new
            {
                machine = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                processors = Environment.ProcessorCount,
                avalonia = typeof(Window).Assembly.GetName().Version?.ToString(),
                markdig = typeof(Markdown).Assembly.GetName().Version?.ToString(),
                configuration =
#if DEBUG
                    "Debug",
#else
                    "Release",
#endif
                backend = "Avalonia.Headless + Skia + Inter",
                // Runtime knobs can materially change results even on the same machine.
                serverGc = System.Runtime.GCSettings.IsServerGC,
                runtimeOptions = new[] { "TieredCompilation", "TieredPGO", "ReadyToRun", "gcServer", "GCHeapHardLimit", "GCConserveMemory" }
                    .SelectMany(option => new[] { "DOTNET_" + option, "COMPlus_" + option })
                    .ToDictionary(option => option, Environment.GetEnvironmentVariable),
            },
            parameters = new { warmups = Warmups, samples = Samples, steps = Steps, paragraphs = Paragraphs,
                codeLines = CodeLines, width = 800, height = 600, alternateWidth = 420 },
            scenarios = results,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("LM_BENCHMARK_OUTPUT") is { Length: > 0 } path)
            File.WriteAllText(path, json);
    }

    private static object Measure(Func<Trial> create)
    {
        using var trial = create(); // setup and cleanup are outside BOTH measurements
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        trial.Run(); // synchronous UI-thread work; no process-wide allocation attribution
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        trial.Check(); // only correctness assertions, with no clock/allocation thresholds
        return new { milliseconds, allocatedBytes = bytes, work = trial.Work() };
    }

    private static Trial Code(bool append)
    {
        var block = new CodeBlock { AutoSyntaxHighlight = false, Language = "csharp" };
        var lines = Enumerable.Range(0, CodeLines + Steps).Select(i => $"var value{i:D3} = Compute({i}, \"fixed text\");").ToArray();
        for (var i = 0; i < CodeLines; i++) block.SetLine(i, lines[i]);
        block.HighlightSyntax();
        long visits = 0;
        return new Trial(() =>
        {
            for (var i = 0; i < Steps; i++)
            {
                var index = append ? CodeLines + i : CodeLines - 1;
                block.SetLine(index, lines[index]);
                block.HighlightSyntax();
                visits += block.LastHighlightLineVisits;
            }
        }, () => new() { ["updates"] = Steps, ["formattingLineVisits"] = visits },
            () => Assert.That(block.Code, Is.EqualTo(string.Join(Environment.NewLine, lines.Take(append ? CodeLines + Steps : CodeLines)))));
    }

    private static Trial Search()
    {
        var renderer = Renderer();
        var window = Host(renderer);
        long matcherCalls = 0;
        renderer.ApplyTextSearch((_, _) => { matcherCalls++; return new[] { new TextHighlightRange(0, 6) }; });
        Refresh();
        matcherCalls = 0;
        var matches = renderer.TextSearchMatches;
        var collections = renderer.RenderedTextBlockCollections;
        var layouts = MarkdownTextBlock.TextLayoutsCreated;
        return new Trial(() => { for (var i = 0; i < Steps; i++) Refresh(); }, () => new()
        {
            ["refreshes"] = Steps, ["matcherCalls"] = matcherCalls,
            ["matchListReplaced"] = ReferenceEquals(matches, renderer.TextSearchMatches) ? 0 : 1,
            ["blockCollections"] = renderer.RenderedTextBlockCollections - collections,
            ["textLayouts"] = MarkdownTextBlock.TextLayoutsCreated - layouts,
        }, () => Assert.That(renderer.TextSearchMatches, Has.Count.EqualTo(Paragraphs)), window.Close);
        void Refresh()
        {
            renderer.InvalidateRenderedTextState();
            renderer.InvalidateMeasure();
            window.UpdateLayout();
        }
    }

    private static Trial Table()
    {
        var grid = new MarkdownTableGrid();
        var cells = Enumerable.Range(0, 4).Select(i =>
        {
            var text = new CountedText { Text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu",
                FontSize = 14, TextWrapping = TextWrapping.Wrap };
            var cell = new Border { Child = text };
            Grid.SetColumn(cell, i % 2);
            Grid.SetRow(cell, i / 2);
            grid.Children.Add(cell);
            return text;
        }).ToArray();
        var window = Host(grid);
        foreach (var cell in cells) cell.Measures = 0;
        var height = cells[0].Bounds.Height;
        return new Trial(() =>
        {
            for (var i = 0; i < Steps; i++)
            {
                cells[0].Height = height + (i % 2 == 0 ? 10 : 20);
                window.UpdateLayout();
            }
        }, () => new() { ["updates"] = Steps, ["changedCellMeasures"] = cells[0].Measures,
            ["unchangedCellMeasures"] = cells.Skip(1).Sum(cell => cell.Measures) },
            () => Assert.That(cells[0].Bounds.Height, Is.EqualTo(height + 20)), window.Close);
    }

    private static Trial Document(bool resize)
    {
        // Parsing belongs to Markdig and is deliberately excluded. Construction includes
        // renderer/control creation, attaching the tree and its first measure/arrange.
        var document = Markdown.Parse(Corpus);
        Window? window = null;
        if (resize) window = Host(new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(document) });
        var layouts = MarkdownTextBlock.TextLayoutsCreated;
        return new Trial(() =>
        {
            if (!resize)
                window = Host(new MarkdownRenderer { DocumentUpdate = new MarkdownDocumentUpdate.Full(document) });
            else
                for (var i = 0; i < Steps; i++)
                {
                    window!.Width = i % 2 == 0 ? 420 : 800;
                    Dispatcher.UIThread.RunJobs(); // process resize notification, never sleep
                    window.UpdateLayout();
                }
        }, () => new() { ["layouts"] = MarkdownTextBlock.TextLayoutsCreated - layouts,
            ["operations"] = resize ? Steps : 1 },
            () => Assert.That(window!.Bounds.Width, Is.EqualTo(800)), () => window?.Close());
    }

    private static MarkdownRenderer Renderer() => new() { DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(Corpus)) };
    private static Window Host(Control content)
    {
        var window = new Window { Width = 800, Height = 600, Content = new ScrollViewer { Content = content } };
        try { window.Show(); window.UpdateLayout(); return window; }
        catch { window.Close(); throw; }
    }

    private sealed class CountedText : MarkdownTextBlock
    {
        internal int Measures;
        protected override global::Avalonia.Size MeasureOverride(global::Avalonia.Size availableSize)
        { Measures++; return base.MeasureOverride(availableSize); }
    }

    private sealed class Trial(Action run, Func<Dictionary<string, long>> work, Action check, Action? cleanup = null) : IDisposable
    {
        internal Action Run => run;
        internal Func<Dictionary<string, long>> Work => work;
        internal Action Check => check;
        public void Dispose() => cleanup?.Invoke();
    }
}
