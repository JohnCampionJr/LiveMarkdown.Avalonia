using System.Collections;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>Repeatable review probes. Reports counts and allocations, never timing verdicts.
/// Run explicitly with --filter FullyQualifiedName~PerformanceAuditProbes. Optional JSON output:
/// LM_PERF_REVIEW_OUTPUT=/absolute/scratch/directory.</summary>
[TestFixture]
[NonParallelizable]
[Explicit("Performance review probes; not regression gates for the current inefficiencies.")]
public class PerformanceAuditProbes
{
    [Test]
    public async Task Audit_rendering_and_layout_work()
    {
        var report = await HeadlessSession.Current.Dispatch(() =>
        {
            var results = new Dictionary<string, object>();
            var table = new MarkdownTableGrid();
            var cells = Enumerable.Range(0, 4).Select(i =>
            {
                var cell = new CountedCell();
                Grid.SetColumn(cell, i % 2);
                Grid.SetRow(cell, i / 2);
                table.Children.Add(cell);
                return cell;
            }).ToArray();
            var tableWindow = new Window { Width = 320, Height = 200, Content = table };
            try
            {
                tableWindow.Show();
                tableWindow.UpdateLayout();
                foreach (var cell in cells) cell.Constraints.Clear();
                cells[0].InvalidateMeasure();
                tableWindow.UpdateLayout();
                results["tableOneCellInvalidated"] = cells.Select((cell, i) => new
                {
                    cell = i, explicitlyInvalidated = i == 0, executions = cell.Constraints.Count,
                    constraints = cell.Constraints.Select(width => double.IsPositiveInfinity(width) ? "infinite" : width.ToString("F1")).ToArray(),
                }).ToArray();
                foreach (var cell in cells) cell.Constraints.Clear();
                cells[0].ContentHeight = 60;
                cells[0].InvalidateMeasure();
                tableWindow.UpdateLayout();
                results["tableOneCellHeightChanged"] = cells.Select((cell, i) => new
                {
                    cell = i, changedContent = i == 0, executions = cell.Constraints.Count,
                    constraints = cell.Constraints.Select(width => double.IsPositiveInfinity(width) ? "infinite" : width.ToString("F1")).ToArray(),
                }).ToArray();
                Assert.That(table.DesiredSize.Width, Is.LessThanOrEqualTo(320));
            }
            finally { tableWindow.Close(); }

            var markdownTable = new MarkdownTableGrid();
            var markdownCells = Enumerable.Range(0, 4).Select(i =>
            {
                var text = new CountedMarkdownCell
                {
                    Text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega",
                    FontSize = 14, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                };
                var cell = new Border { Child = text };
                Grid.SetColumn(cell, i % 2);
                Grid.SetRow(cell, i / 2);
                markdownTable.Children.Add(cell);
                return text;
            }).ToArray();
            var markdownWindow = new Window { Width = 320, Height = 1000, Content = markdownTable };
            try
            {
                markdownWindow.Show();
                markdownWindow.UpdateLayout();
                foreach (var cell in markdownCells) cell.Measures = 0;
                markdownCells[0].Height = markdownCells[0].Bounds.Height + 30;
                markdownWindow.UpdateLayout();
                results["tableMarkdownCellHeightChanged"] = markdownCells.Select((cell, i) => new
                {
                    cell = i, changedContent = i == 0, executions = cell.Measures,
                }).ToArray();
            }
            finally { markdownWindow.Close(); }

            var code = new CodeBlock { AutoSyntaxHighlight = false, Language = "csharp" };
            const string sourceLine = "var message = Compute(42, \"unchanged text\");";
            code.SetLine(0, sourceLine);
            code.HighlightSyntax();
            var before = code.Inlines[0];
            code.SetLine(0, sourceLine);
            var afterSet = code.Inlines[0];
            code.HighlightSyntax();
            results["sameCodeLine"] = new
            {
                originalInlineType = before.GetType().Name,
                afterSetInlineType = afterSet.GetType().Name,
                retainedInline = ReferenceEquals(before, afterSet),
                codePreserved = code.Code == sourceLine,
            };
            Assert.That(code.Code, Is.EqualTo(sourceLine));

            results["highlightTailAppend"] = new[] { 32, 256 }.Select(count =>
            {
                var block = new CodeBlock { AutoSyntaxHighlight = false };
                for (var i = 0; i < count; i++) block.SetLine(i, $"// review line {i}");
                var highlighter = SyntaxHighlighting.Create("csharp");
                var lines = new CountedLines(block.LineTexts);
                Format();
                block.SetLine(count, "// appended line");
                lines.Reads = 0;
                Format();
                var firstAppendReads = lines.Reads;
                block.SetLine(count + 1, "// another appended line");
                lines.Reads = 0;
                Format();
                return new { originalLines = count, firstAppendReads, nextAppendReads = lines.Reads };
                void Format() => highlighter.FormatInlines(block.CodeTextBlocks.Select(b => b.Inlines!).ToArray(), knownLineTexts: lines);
            }).ToArray();

            results["highlightCodeBlockTailAppend"] = new[] { 32, 256 }.Select(count =>
            {
                var block = new CodeBlock { AutoSyntaxHighlight = false, Language = "csharp" };
                for (var i = 0; i < count; i++) block.SetLine(i, $"// production pipeline line {i}");
                block.HighlightSyntax();
                block.SetLine(count, "// appended line");
                block.HighlightSyntax();
                var firstAppendVisits = block.LastHighlightLineVisits;
                block.SetLine(count + 1, "// next appended line");
                block.HighlightSyntax();
                return new { originalLines = count, firstAppendVisits, nextAppendVisits = block.LastHighlightLineVisits };
            }).ToArray();

            results["searchRefresh"] = new[] { 20, 200 }.Select(SearchRefresh).ToArray();
            var tokenCache = typeof(SyntaxHighlighting).GetField("BlockCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var countProperty = tokenCache.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            int CacheCount() => (int)countProperty.GetValue(tokenCache)!;
            var tokenCap = (int)typeof(SyntaxHighlighting).GetField("BlockCacheCap", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            var drops = new List<object>();
            var tokenizer = SyntaxHighlighting.Create("csharp");
            for (var i = 0; i < tokenCap + 1; i++)
            {
                var countBefore = CacheCount();
                tokenizer.Prewarm($"// unique audit cache line {i}");
                if (CacheCount() < countBefore) drops.Add(new { before = countBefore, after = CacheCount() });
            }
            results["tokenCacheCapacity"] = new { capacity = tokenCap, countDrops = drops };
            return results;
        }, CancellationToken.None);
        Publish("rendering", report);
    }

    private static object SearchRefresh(int count)
    {
        var renderer = new MarkdownRenderer
        {
            DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(string.Join("\n\n", Enumerable.Range(0, count).Select(i => $"needle paragraph {i}")))),
        };
        var window = new Window { Width = 400, Height = 300, Content = new ScrollViewer { Content = renderer } };
        try
        {
            window.Show();
            window.UpdateLayout();
            var matcherCalls = 0;
            renderer.ApplyTextSearch((_, _) => { matcherCalls++; return new[] { new TextHighlightRange(0, 6) }; });
            renderer.InvalidateRenderedTextState();
            renderer.InvalidateMeasure();
            window.UpdateLayout(); // warm the unchanged refresh path before counting allocations
            matcherCalls = 0;
            var beforeMatches = renderer.TextSearchMatches;
            var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
            {
                renderer.InvalidateRenderedTextState();
                renderer.InvalidateMeasure();
                window.UpdateLayout();
            }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            Assert.That(renderer.TextSearchMatches, Has.Count.EqualTo(count));
            return new { paragraphs = count, refreshes = 5, matcherCalls, bytes, replacedMatches = !ReferenceEquals(beforeMatches, renderer.TextSearchMatches) };
        }
        finally { window.Close(); }
    }

    [Test]
    public async Task Audit_image_cache_limits_and_retention()
    {
        var oldLimit = RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes;
        try
        {
            RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = 1024;
            var cache = new RamBasedAsyncImageLoaderCache();
            var uri = new Uri("https://example.invalid/review-image.png");
            using var source = new CountedStream(new byte[64 * 1024]);
            await cache.SetAsync(uri, source, AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);
            var rejected = await cache.GetAsync(uri, CancellationToken.None);
            Assert.That(rejected, Is.Null);
            using var unknownLength = new CountedStream(new byte[64 * 1024], seekable: false);
            await cache.SetAsync(uri, unknownLength, AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);

            RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = oldLimit;
            var second = new RamBasedAsyncImageLoaderCache();
            await Seed(second, uri);
            using var retainedRead = await second.GetAsync(uri, CancellationToken.None);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var afterGc = await second.GetAsync(uri, CancellationToken.None);
            Publish("image-cache", new
            {
                limit = 1024, inputBytes = 64 * 1024, bytesReadBeforeReject = source.ReadBytes,
                unknownLengthBytesReadBeforeReject = unknownLength.ReadBytes,
                retainedReader = retainedRead is not null, cacheHitWithRetainedReaderAfterGc = afterGc is not null,
            });
            GC.KeepAlive(retainedRead);
        }
        finally { RamBasedAsyncImageLoaderCache.MaxEntrySizeBytes = oldLimit; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task Seed(RamBasedAsyncImageLoaderCache cache, Uri uri) =>
        cache.SetAsync(uri, new MemoryStream(new byte[256]), AsyncImageLoaderCacheMetadata.Create(uri), CancellationToken.None);

    private static void Publish(string name, object report)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("LM_PERF_REVIEW_OUTPUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        }
    }

    private sealed class CountedCell : Control
    {
        internal List<double> Constraints { get; } = [];
        internal double ContentHeight { get; set; } = 30;
        protected override Size MeasureOverride(Size availableSize)
        {
            Constraints.Add(availableSize.Width);
            return new(Math.Min(500, availableSize.Width), ContentHeight);
        }
    }

    private sealed class CountedMarkdownCell : MarkdownTextBlock
    {
        internal int Measures;
        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    private sealed class CountedLines(IReadOnlyList<string> source) : IReadOnlyList<string>
    {
        public int Reads;
        public string this[int index] { get { Reads++; return source[index]; } }
        public int Count => source.Count;
        public IEnumerator<string> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountedStream(byte[] bytes, bool seekable = true) : MemoryStream(bytes)
    {
        public override bool CanSeek => seekable;
        public long ReadBytes;
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            var before = Position;
            await base.CopyToAsync(destination, bufferSize, cancellationToken);
            ReadBytes += Position - before;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += read;
            return read;
        }
    }
}
