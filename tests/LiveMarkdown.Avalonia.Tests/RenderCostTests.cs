using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// What a layout COSTS, and what it produces, held to two different standards.
/// </summary>
/// <remarks>
/// <para>The structural tests are exact: a text source feeding the formatter has one right answer,
/// and it is the lines it produces. They exist because the run lookup behind them was rewritten for
/// speed, and nothing else in the suite would notice a lookup that returned a neighbouring run —
/// the text would still be there, in the wrong place, on a line nobody asserts.</para>
///
/// <para>The allocation ceilings are bounds, not measurements. Bytes allocated for a fixed amount of
/// work is a COUNT: it does not care what else the machine is doing, which is what makes it gateable
/// where wall time is not. They are set well above what the code does today, so they catch a change
/// of kind — a cache that stopped hitting, a layout rebuilt per frame — rather than drift.</para>
///
/// <para>Both need the Skia backend that <see cref="MarkdownPointerSelectionTests.StyledTestApplication"/>
/// sets up. Under the headless drawing stub every Bold or Italic lookup allocates an uncached 65 KB
/// synthetic typeface, which puts one styled paragraph at 692 KB and makes every ceiling here
/// meaningless.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class RenderCostTests
{
    private const string WrappingParagraph =
        "Paragraph one has **bold item text**, *emphasis*, a [link](https://example.com/1), and " +
        "`inline1` code so that the paragraph wraps across several lines inside a narrow window.";

    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession() => session = HeadlessSession.Current;

    // ------------------------------------------------------------------ structure

    [Test]
    public async Task A_code_block_lays_out_one_line_per_source_line()
    {
        var lines = await session.Dispatch(
            () =>
            {
                var markdown = new StringBuilder("```csharp\n");
                for (var i = 0; i < 12; i++)
                {
                    markdown.Append($"    var value{i} = Compute({i}, \"text {i}\");\n");
                }

                markdown.Append("```\n");

                using var host = new LayoutHost(markdown.ToString());
                return host.LinesOfCodeBlock();
            },
            CancellationToken.None);

        Assert.That(lines, Has.Count.EqualTo(12), "one visual line per source line, no wrapping at this width");
        for (var i = 0; i < lines.Count; i++)
        {
            Assert.That(lines[i], Does.Contain($"var value{i} ="), $"line {i} carries its own source line");
            Assert.That(lines[i], Does.Contain($"\"text {i}\""));
        }
    }

    [Test]
    public async Task A_wrapping_paragraph_lays_out_its_text_in_order()
    {
        var lines = await session.Dispatch(
            () =>
            {
                using var host = new LayoutHost(WrappingParagraph, width: 320);
                return host.LinesOfLastBlock();
            },
            CancellationToken.None);

        Assert.That(lines, Has.Count.GreaterThan(1), "the paragraph is meant to wrap at this width");

        // Joined, the visual lines are the block's text: nothing dropped, nothing repeated, in order.
        var joined = string.Concat(lines).Replace(" ", " ");
        Assert.That(joined, Does.StartWith("Paragraph one has bold item text"));
        Assert.That(joined, Does.EndWith("inside a narrow window."));
        Assert.That(joined, Does.Contain("inline1"), "the code chip keeps its place in the run order");
    }

    [Test]
    public async Task A_long_code_block_is_split_across_text_blocks_and_keeps_every_line()
    {
        const int count = 40;
        var result = await session.Dispatch(
            () =>
            {
                var markdown = new StringBuilder("```csharp\n");
                for (var i = 0; i < count; i++) markdown.Append($"    var value{i} = Compute({i});\n");
                markdown.Append("```\n");

                using var host = new LayoutHost(markdown.ToString());
                var block = host.CodeBlock;
                return (
                    Blocks: block.CodeTextBlocks.Count,
                    Lines: block.LineCount,
                    Code: block.Code,
                    Text: string.Join("\n", block.CodeTextBlocks.Select(b => b.ActualText)));
            },
            CancellationToken.None);

        var expectedBlocks = (count + CodeBlock.LinesPerChunk - 1) / CodeBlock.LinesPerChunk;
        Assert.Multiple(() =>
        {
            Assert.That(result.Blocks, Is.EqualTo(expectedBlocks), "one text block per group of lines");
            Assert.That(result.Lines, Is.EqualTo(count));
        });

        // Every line present, once, in order -- across the blocks it is laid out in.
        for (var i = 0; i < count; i++)
        {
            Assert.That(result.Code, Does.Contain($"var value{i} = Compute({i});"), $"line {i} in Code");
            Assert.That(result.Text, Does.Contain($"var value{i} = Compute({i});"), $"line {i} rendered");
        }

        var lines = result.Code!.Split('\n');
        Assert.That(lines, Has.Length.EqualTo(count), "no line duplicated or lost at a boundary");
    }

    [Test]
    public async Task Appending_to_a_long_code_block_touches_only_the_last_text_block()
    {
        // The whole point of splitting: a line arriving at the end must not disturb the layout of
        // the blocks before it, because re-measuring those is what made a streaming block quadratic.
        var result = await session.Dispatch(
            () =>
            {
                var markdown = new StringBuilder("```csharp\n");
                for (var i = 0; i < 40; i++) markdown.Append($"    var value{i} = Compute({i});\n");
                var first = markdown.ToString();
                var second = first + "    var extra = Compute(99);\n";

                var documentNode = new DocumentNode(new MarkdownRenderer());
                documentNode.Update(
                    documentNode,
                    Markdown.Parse(first, MarkdownUpdateProducer.DefaultPipeline),
                    new ObservableStringBuilderChangedEventArgs(0, first.Length, first.Length, 1),
                    CancellationToken.None);

                var block = documentNode.Control.GetLogicalDescendants().OfType<CodeBlock>().Single();
                var before = block.CodeTextBlocks.Select(b => b.ActualText).ToArray();

                documentNode.Update(
                    documentNode,
                    Markdown.Parse(second, MarkdownUpdateProducer.DefaultPipeline),
                    new ObservableStringBuilderChangedEventArgs(first.Length, second.Length - first.Length, second.Length, 2),
                    CancellationToken.None);

                var after = block.CodeTextBlocks.Select(b => b.ActualText).ToArray();
                return (Before: before, After: after);
            },
            CancellationToken.None);

        Assert.That(result.After[^1], Does.Contain("var extra = Compute(99);"), "the line arrived");
        for (var i = 0; i < result.Before.Length - 1; i++)
        {
            Assert.That(result.After[i], Is.EqualTo(result.Before[i]), $"text block {i} was left alone");
        }
    }

    [Test]
    public async Task A_code_block_with_a_blank_line_keeps_the_blank_line()
    {
        // A blank line is a zero-length run, which the run index deliberately leaves out. It still
        // has to occupy a visual line.
        var lines = await session.Dispatch(
            () =>
            {
                using var host = new LayoutHost("```csharp\nfirst();\n\nlast();\n```\n");
                return host.LinesOfCodeBlock();
            },
            CancellationToken.None);

        Assert.That(lines, Has.Count.EqualTo(3));
        Assert.That(lines[0], Does.Contain("first()"));
        Assert.That(lines[1].Trim(), Is.Empty);
        Assert.That(lines[2], Does.Contain("last()"));
    }

    [Test]
    public async Task A_streaming_code_block_highlights_once_per_update()
    {
        // The control re-highlights whenever its inlines change, and a node rewriting a block hands
        // it one mutation per line and per break. Without holding those off, appending a single line
        // to a block walked the whole block several times to reach the answer one walk gives.
        var passes = await session.Dispatch(
            () =>
            {
                const string first = "```csharp\nvar a = 1;\nvar b = 2;\n";
                const string second = first + "var c = 3;\n";

                var documentNode = new DocumentNode(new MarkdownRenderer());
                documentNode.Update(
                    documentNode,
                    Markdown.Parse(first, MarkdownUpdateProducer.DefaultPipeline),
                    new ObservableStringBuilderChangedEventArgs(0, first.Length, first.Length, 1),
                    CancellationToken.None);

                var block = documentNode.Control.GetLogicalDescendants().OfType<CodeBlock>().Single();
                var before = block.SyntaxHighlightPassCount;

                documentNode.Update(
                    documentNode,
                    Markdown.Parse(second, MarkdownUpdateProducer.DefaultPipeline),
                    new ObservableStringBuilderChangedEventArgs(first.Length, second.Length - first.Length, second.Length, 2),
                    CancellationToken.None);

                return (Passes: block.SyntaxHighlightPassCount - before, block.Code);
            },
            CancellationToken.None);

        Assert.That(passes.Passes, Is.EqualTo(1), "one appended line, one walk of the block");
        Assert.That(passes.Code, Does.Contain("var c = 3;"), "and the line actually arrived");
    }

    // -------------------------------------------------------------------- search

    [Test]
    public async Task Streaming_leaves_the_search_highlight_of_an_unchanged_block_alone()
    {
        // A named highlight carrying a foreground rebuilds its block's text layout when it changes,
        // so re-applying the search to a block whose matches did not move is a relayout bought for
        // nothing -- and a document update touches every matched block at once.
        var result = await session.Dispatch(
            () =>
            {
                var host = new SearchHost("item");
                try
                {
                    host.Append("First item here.\n\n");
                    host.Append("Second item here.\n\n");
                    host.Append("Third item here.\n\n");

                    var blocks = host.Blocks();
                    var changes = 0;
                    void Count(object? sender, EventArgs e) => changes++;
                    blocks[0].Highlights.Changed += Count;

                    host.Append("Fourth item here.\n\n");

                    blocks[0].Highlights.Changed -= Count;
                    return (Changes: changes, Matches: host.Renderer.TextSearchMatches.Count);
                }
                finally
                {
                    host.Dispose();
                }
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Changes, Is.Zero, "the first block's highlight was left where it was");
            Assert.That(result.Matches, Is.EqualTo(4), "and every paragraph is still matched, including the new one");
        });
    }

    [Test]
    public async Task A_block_that_stops_matching_loses_its_highlight()
    {
        // The other half of not tearing the search down: nothing clears a stale highlight now except
        // the diff, so a block whose text no longer matches has to lose it there.
        var result = await session.Dispatch(
            () =>
            {
                var host = new SearchHost("item");
                try
                {
                    host.Append("First item here.\n\n");
                    host.Append("Second item here.\n\n");
                    var before = host.Blocks()[1].Highlights.Count;

                    // Rewrite the whole document with the term gone from it.
                    host.Replace("First item here.\n\nSecond thing here.\n\n");

                    var blocks = host.Blocks();
                    return (Before: before, Second: blocks[1].Highlights.Count, Matches: host.Renderer.TextSearchMatches.Count);
                }
                finally
                {
                    host.Dispose();
                }
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Before, Is.EqualTo(1), "sanity: it matched to begin with");
            Assert.That(result.Second, Is.Zero, "and the highlight is gone once the text stops matching");
            Assert.That(result.Matches, Is.EqualTo(1));
        });
    }

    /// <summary>A realised renderer, fed one append at a time, optionally with a search running.</summary>
    private sealed class SearchHost : IDisposable
    {
        private readonly Window window;
        private readonly StringBuilder source = new();
        private MarkdownDocumentUpdate? previous;
        private long version;

        public MarkdownRenderer Renderer { get; }

        public SearchHost(string? query)
        {
            Renderer = new MarkdownRenderer();
            var styles = new TextHighlightStyles();
            styles.Set(
                MarkdownRenderer.DefaultTextSearchHighlightName,
                new TextHighlightStyle { Background = Brushes.Yellow, Foreground = Brushes.Red });
            MarkdownTextBlock.SetHighlightStyles(Renderer, styles);

            window = new Window { Width = 600, Height = 400, Content = Renderer };
            window.Show();
            window.UpdateLayout();
            if (query is not null) Renderer.ApplyTextSearch(query);
        }

        public void Append(string text)
        {
            Post(text);
            Pump();
        }

        /// <summary>Bytes the layout following an append allocates, with the parse left outside.</summary>
        public long AppendedLayoutBytes(string text)
        {
            Post(text);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Pump();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private void Post(string text)
        {
            var startIndex = source.Length;
            source.Append(text);
            Commit(new ObservableStringBuilderChangedEventArgs(startIndex, text.Length, source.Length, ++version));
        }

        public void Replace(string text)
        {
            var oldLength = source.Length;
            source.Clear();
            source.Append(text);
            Commit(new ObservableStringBuilderChangedEventArgs(0, Math.Max(oldLength, text.Length), source.Length, ++version));
            Pump();
        }

        private void Commit(ObservableStringBuilderChangedEventArgs change)
        {
            var document = Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline);
            var update = previous is null
                ? new MarkdownDocumentUpdate.Full(document, change.Version)
                : (MarkdownDocumentUpdate)new MarkdownDocumentUpdate.Incremental(previous, document, change);

            Renderer.DocumentUpdate = update;
            previous = update;
        }

        private void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        public MarkdownTextBlock[] Blocks() => [.. Renderer.GetVisualDescendants().OfType<MarkdownTextBlock>()];

        public void Dispose() => window.Close();
    }

    [Test]
    public async Task Laying_out_an_append_costs_the_same_in_a_long_document_as_in_a_short_one()
    {
        // Every completed layout used to walk the renderer for its text blocks and copy each one's
        // layout text into a MarkdownTextProjection, whether or not any host had ever read the
        // property -- so the cheapest thing a keystroke could cost grew with the whole document
        // rather than the block it changed. Nothing reads the projection here, so nothing should
        // build one, and what a layout costs should stop at the paragraph that changed.
        var result = await session.Dispatch(
            () => (Short: LayoutCost(blocks: 100), Long: LayoutCost(blocks: 800)),
            CancellationToken.None);

        Assert.That(
            result.Long,
            Is.LessThan(result.Short * 1.4),
            $"laying out an append cost {result.Long / 1024} KB in a long document against "
                + $"{result.Short / 1024} KB in a short one");
    }

    /// <summary>
    /// An active search re-runs on every completed layout, over every block the renderer owns, because a
    /// layout is the only point at which the text is settled. Streaming a token changes ONE block, so
    /// re-matching the rest re-derives an answer that cannot have changed.
    /// </summary>
    /// <remarks>Counted rather than weighed on purpose. The allocation this saves is real — 254 KB per
    /// append into a 400-paragraph document, against 39 KB for the same append with no search — but a
    /// ceiling would pass again the day the matcher gets cheaper while still running everywhere. What is
    /// actually being asserted is that the work is proportional to what CHANGED, and invocations say that
    /// exactly.</remarks>
    [Test]
    public async Task An_active_search_only_re_matches_the_blocks_that_changed()
    {
        var counted = await session.Dispatch(
            () =>
            {
                using var host = new SearchHost(query: null);
                for (var i = 0; i < 100; i++)
                {
                    host.Append($"Paragraph {i} of the document, with enough words in it to wrap.\n\n");
                }

                var calls = 0;
                host.Renderer.ApplyTextSearch((_, _) =>
                {
                    calls++;
                    return [];
                });

                // Installing the search matches the whole document once, which it must.
                var firstPass = calls;

                calls = 0;
                host.Append("One more paragraph, with a comparable number of words in it.\n\n");
                return (FirstPass: firstPass, AfterOneAppend: calls);
            },
            CancellationToken.None);

        Assert.That(counted.FirstPass, Is.GreaterThan(50), "installing a search must match the whole document");
        Assert.That(
            counted.AfterOneAppend,
            Is.LessThan(5),
            $"appending one paragraph re-matched {counted.AfterOneAppend} blocks, having matched "
                + $"{counted.FirstPass} when the search was installed");
    }

    /// <summary>Bytes the layout after one append allocates in a document of the given size.</summary>
    private static long LayoutCost(int blocks)
    {
        using var host = new SearchHost(query: null);
        for (var i = 0; i < blocks; i++)
        {
            host.Append($"Paragraph {i} of the document, with enough words in it to wrap.\n\n");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 8;
        long allocated = 0;
        for (var i = 0; i < iterations; i++)
        {
            allocated += host.AppendedLayoutBytes(
                $"Appended paragraph {i} with a comparable number of words in it.\n\n");
        }

        return allocated / iterations;
    }

    [Test]
    public async Task Appending_to_a_long_code_block_costs_what_appending_to_a_short_one_does()
    {
        // A streaming block only ever grows, so the work of adding a line must not depend on how
        // many lines are already there. Highlighting used to index the whole block on every append
        // to find the one line that needed formatting, which made this ratio climb with length:
        // 62.8 KB at 116 lines, 74.9 at 416, 123.3 at 1616.
        var result = await session.Dispatch(
            () => (Short: AppendCost(existing: 100), Long: AppendCost(existing: 800)),
            CancellationToken.None);

        Assert.That(
            result.Long,
            Is.LessThan(result.Short * 1.4),
            $"appending to a long block cost {result.Long / 1024} KB against {result.Short / 1024} KB for a short one");
    }

    [Test]
    public void Appending_to_a_long_paragraph_costs_the_node_sync_what_a_short_one_does()
    {
        // Prose streams a sentence at a time into ONE paragraph, so the block an append lands in
        // grows without bound. Laying it out again is Avalonia's cost and it is linear in the
        // paragraph -- 334 KB an append at three thousand characters, 1,459 KB at ten thousand, and a
        // bare SelectableTextBlock behaves the same way. What this pins is the half that is OURS:
        // the node sync must keep costing the sentence rather than the paragraph.
        var result = (Short: SentenceAppendCost(existing: 10), Long: SentenceAppendCost(existing: 200));

        Assert.That(
            result.Long,
            Is.LessThan(result.Short * 1.4),
            $"appending to a long paragraph cost {result.Long / 1024} KB of node sync against "
                + $"{result.Short / 1024} KB for a short one");
    }

    /// <summary>Bytes the node sync allocates for one sentence appended to a paragraph of some length.</summary>
    private static long SentenceAppendCost(int existing)
    {
        var documentNode = new DocumentNode(new MarkdownRenderer());
        var source = new StringBuilder();
        long version = 0;

        void Commit(string added)
        {
            var startIndex = source.Length - added.Length;
            var change = new ObservableStringBuilderChangedEventArgs(startIndex, added.Length, source.Length, ++version);
            documentNode.Update(
                documentNode,
                Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline),
                change,
                CancellationToken.None);
        }

        for (var i = 0; i < existing; i++)
        {
            var sentence = ProseSentence(i);
            source.Append(sentence);
            Commit(sentence);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 8;
        long allocated = 0;
        for (var i = 0; i < iterations; i++)
        {
            var sentence = ProseSentence(existing + i);
            source.Append(sentence);
            var startIndex = source.Length - sentence.Length;
            var change = new ObservableStringBuilderChangedEventArgs(startIndex, sentence.Length, source.Length, ++version);
            var document = Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline);

            var before = GC.GetAllocatedBytesForCurrentThread();
            documentNode.Update(documentNode, document, change, CancellationToken.None);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return allocated / iterations;
    }

    private static string ProseSentence(int i) =>
        $"Sentence {i} of the reply, with **bold** and `code` in it, long enough to matter. ";

    /// <summary>Bytes the node sync allocates for one line appended to a block that already has some.</summary>
    private static long AppendCost(int existing)
    {
        var documentNode = new DocumentNode(new MarkdownRenderer());
        var source = new StringBuilder("```csharp\n");
        long version = 0;

        void Commit(string added)
        {
            var startIndex = source.Length - added.Length;
            var change = new ObservableStringBuilderChangedEventArgs(startIndex, added.Length, source.Length, ++version);
            documentNode.Update(
                documentNode,
                Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline),
                change,
                CancellationToken.None);
        }

        Commit("```csharp\n");
        for (var i = 0; i < existing; i++)
        {
            var line = CodeLine(i);
            source.Append(line);
            Commit(line);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 8;
        long allocated = 0;
        for (var i = 0; i < iterations; i++)
        {
            var line = CodeLine(existing + i);
            source.Append(line);
            var startIndex = source.Length - line.Length;
            var change = new ObservableStringBuilderChangedEventArgs(startIndex, line.Length, source.Length, ++version);
            var document = Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline);

            var before = GC.GetAllocatedBytesForCurrentThread();
            documentNode.Update(documentNode, document, change, CancellationToken.None);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return allocated / iterations;
    }

    private static string CodeLine(int i) =>
        $"    var value{i} = Compute({i}, \"text {i}\") + items[{i % 7}].Name; // trailing comment {i}\n";

    // ----------------------------------------------------------------- allocation

    [TestCase("plain paragraph", "Paragraph one has plain text that wraps across a few lines inside the window.", 40 * 1024)]
    [TestCase("rich paragraph", WrappingParagraph, 120 * 1024)]
    public async Task A_paragraph_relayout_stays_under_its_ceiling(string label, string markdown, int ceiling)
    {
        var bytes = await session.Dispatch(
            () =>
            {
                using var host = new LayoutHost(markdown, width: 320);
                return host.BytesPerRelayout();
            },
            CancellationToken.None);

        Assert.That(bytes, Is.LessThan(ceiling), $"{label}: {bytes / 1024} KB per relayout");
    }

    [Test]
    public async Task A_code_block_relayout_stays_under_its_ceiling()
    {
        var bytes = await session.Dispatch(
            () =>
            {
                var markdown = new StringBuilder("```csharp\n");
                for (var i = 0; i < 30; i++)
                {
                    markdown.Append($"    var value{i} = Compute({i}, \"text {i}\") + items[{i % 7}].Name;\n");
                }

                markdown.Append("```\n");

                using var host = new LayoutHost(markdown.ToString());
                return host.BytesPerRelayout();
            },
            CancellationToken.None);

        // Thirty highlighted lines. Almost all of this is shaping every run of every line again, which
        // is what caching shaped code lines is meant to take away; lower the ceiling when it does.
        Assert.That(bytes, Is.LessThan(4 * 1024 * 1024), $"{bytes / 1024} KB per relayout");
    }

    /// <summary>A realised renderer over one markdown document, and the block it produced.</summary>
    private sealed class LayoutHost : IDisposable
    {
        private readonly Window window;
        private readonly MarkdownRenderer renderer;

        public LayoutHost(string markdown, double width = 800)
        {
            renderer = new MarkdownRenderer
            {
                DocumentUpdate = new MarkdownDocumentUpdate.Full(
                    Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline)),
            };
            window = new Window { Width = width, Height = 600, Content = renderer };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public CodeBlock CodeBlock =>
            renderer.GetVisualDescendants().OfType<CodeBlock>().Single();

        private MarkdownTextBlock Block =>
            renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().Last();

        /// <summary>The visual lines of every text block the code is laid out in, in order.</summary>
        public List<string> LinesOfCodeBlock()
        {
            var lines = new List<string>();
            foreach (var block in CodeBlock.CodeTextBlocks) lines.AddRange(LinesOf(block));
            return lines;
        }

        /// <summary>The text of each visual line the block's layout produced.</summary>
        public List<string> LinesOfLastBlock() => LinesOf(Block);

        private static List<string> LinesOf(MarkdownTextBlock block)
        {
            var text = block.LayoutText;
            var lines = new List<string>();
            foreach (var line in block.TextLayout.TextLines)
            {
                var start = Math.Clamp(line.FirstTextSourceIndex, 0, text.Length);
                var length = Math.Clamp(line.Length, 0, text.Length - start);
                lines.Add(text.Substring(start, length).TrimEnd('\r', '\n'));
            }

            return lines;
        }

        /// <summary>Bytes the UI thread allocates for one invalidated measure of the block.</summary>
        public long BytesPerRelayout()
        {
            var block = Block;
            const int iterations = 16;

            // Warm: the first layout resolves typefaces and builds caches that later ones reuse.
            for (var i = 0; i < 4; i++)
            {
                block.InvalidateMeasure();
                window.UpdateLayout();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                block.InvalidateMeasure();
                window.UpdateLayout();
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }

        public void Dispose() => window.Close();
    }
}
