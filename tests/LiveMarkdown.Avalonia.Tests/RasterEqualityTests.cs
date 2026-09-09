using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// Optimizations that are supposed to be invisible, rendered both ways and compared pixel for pixel.
/// </summary>
/// <remarks>
/// <para>Nothing else catches this class of mistake. Merging two runs into one is correct only while
/// the merge cannot change what is painted, and a test that asserts run counts or text passes through
/// every way of getting that wrong -- a background, an underline or a strikethrough carried across a
/// boundary that used to end puts ink where there was none, and the text is identical either way.</para>
///
/// <para>Raw BGRA, deliberately, not encoded bytes. Comparing PNGs compares the compressor as much
/// as the picture.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public class RasterEqualityTests
{
    private const int Width = 900;
    private const int Height = 420;

    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession() => session = HeadlessSession.Current;

    private static string CodeMarkdown()
    {
        var markdown = new StringBuilder("```csharp\n");
        markdown.Append("    // a comment line, all one colour\n");
        markdown.Append("    var value = Compute(1, \"text\") + items[2].Name;\n");
        markdown.Append("    if (value > 0 && other != null) { Run(value, other); }\n");
        markdown.Append("\tpublic static async Task<int> Method(string first, int second)\n");
        markdown.Append("    return new [] { 1, 2, 3 }.Select(x => x * 2).ToList();\n");
        return markdown.Append("```\n").ToString();
    }

    [Test]
    public async Task MergingIdenticallyStyledRunsDoesNotChangeAPixel()
    {
        var result = await session.Dispatch(
            () =>
            {
                var markdown = CodeMarkdown();

                var merged = Render(markdown, mergeRuns: true);
                var perToken = Render(markdown, mergeRuns: false);

                var differing = 0;
                for (var i = 0; i < merged.Length; i++)
                {
                    if (merged[i] != perToken[i]) differing++;
                }

                var ink = 0;
                for (var i = 3; i < merged.Length; i += 4)
                {
                    if (merged[i] != 0) ink++;
                }

                return (Differing: differing, Ink: ink, Total: merged.Length);
            },
            CancellationToken.None);

        Assert.That(result.Ink, Is.GreaterThan(0), "sanity: something was actually drawn");
        Assert.That(
            result.Differing,
            Is.Zero,
            $"{result.Differing} of {result.Total} bytes differ between the merged and one-run-per-token renderings");
    }

    [Test]
    public async Task SplittingACodeBlockAcrossTextBlocksDoesNotChangeAPixel()
    {
        // Includes a block comment that opens in one chunk and closes in another, which is the thing
        // splitting could quietly break: tokenizing is stateful, and a chunk formatted on its own
        // would end the comment at a boundary that exists only because of how the code is laid out.
        var result = await session.Dispatch(
            () =>
            {
                var markdown = new StringBuilder("```csharp\n");
                for (var i = 0; i < 6; i++) markdown.Append($"    var value{i} = Compute({i});\n");
                markdown.Append("    /* a block comment that opens here\n");
                for (var i = 0; i < 6; i++) markdown.Append($"       and keeps going, line {i}, still a comment\n");
                markdown.Append("       and closes down here */\n");
                for (var i = 0; i < 6; i++) markdown.Append($"    var after{i} = Compute({i});\n");
                markdown.Append("```\n");

                var chunked = Render(markdown.ToString(), linesPerChunk: 4);
                var single = Render(markdown.ToString(), linesPerChunk: int.MaxValue);

                var differing = 0;
                for (var i = 0; i < chunked.Length; i++)
                {
                    if (chunked[i] != single[i]) differing++;
                }

                return (Differing: differing, Total: chunked.Length);
            },
            CancellationToken.None);

        Assert.That(
            result.Differing,
            Is.Zero,
            $"{result.Differing} of {result.Total} bytes differ between the split and single-block renderings");
    }

    [Test]
    public async Task AStreamedDocumentRendersLikeAWholeOne()
    {
        // Arriving a few characters at a time must reach the same picture as arriving all at once.
        // Every cache in this file's subject matter -- shaped chips, tokenized lines, and any layout
        // reuse attempted later -- fails in exactly this direction when a content change does not
        // reach it: the block goes on drawing what it used to say, and no assertion about text,
        // ranges or counts notices.
        var result = await session.Dispatch(
            () =>
            {
                var final = "First paragraph here.\n\nSecond paragraph, longer than the one before it.\n\n";

                // Once by streaming it in a piece at a time, once rendered whole. Same pixels.
                var streamed = RenderStreamed(final);
                var whole = Render(final);

                var differing = 0;
                for (var i = 0; i < streamed.Length; i++)
                {
                    if (streamed[i] != whole[i]) differing++;
                }

                return (Differing: differing, Total: streamed.Length);
            },
            CancellationToken.None);

        Assert.That(result.Differing, Is.Zero, $"{result.Differing} of {result.Total} bytes differ between streamed and whole");
    }

    [Test]
    public async Task ADocumentSplicedByTheProducerRendersLikeAWholeOne()
    {
        // The other streaming test parses the whole source on every append. This one goes through the
        // real producer, which parses only the trailing region and splices it onto the document it
        // already had -- so the picture here is drawn from blocks parsed at a dozen different times,
        // against one drawn from a single parse. Anything the splice gets wrong about a span, a
        // heading identifier or the order of the children shows up as ink in the wrong place.
        var result = await session.Dispatch(
            () =>
            {
                var pieces = new[]
                {
                    "# A streamed document\n\n",
                    "First paragraph, with **bold** and `code` in it.\n\n",
                    "- one item\n",
                    "- another item\n",
                    "\n```csharp\nvar value = Compute(1);\n```\n\n",
                    "## A later heading\n\n",
                    "Closing paragraph, longer than the one before it.\n\n",
                };

                var streamed = RenderThroughProducer(pieces, out var splices);
                var whole = Render(string.Concat(pieces));

                var differing = 0;
                for (var i = 0; i < streamed.Length; i++)
                {
                    if (streamed[i] != whole[i]) differing++;
                }

                return (Differing: differing, Total: streamed.Length, Splices: splices);
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Splices, Is.GreaterThan(0), "sanity: the producer actually spliced");
            Assert.That(
                result.Differing,
                Is.Zero,
                $"{result.Differing} of {result.Total} bytes differ between spliced and whole");
        });
    }

    /// <summary>Renders the pieces by feeding them to a real builder and producer, one at a time.</summary>
    private static byte[] RenderThroughProducer(string[] pieces, out int splices)
    {
        var builder = new ObservableStringBuilder();
        var producer = new MarkdownUpdateProducer { MarkdownBuilder = builder };
        var renderer = new MarkdownRenderer { UpdateProducer = producer };
        var window = new Window { Width = Width, Height = Height, Background = Brushes.White, Content = renderer };
        try
        {
            window.Show();
            window.UpdateLayout();

            foreach (var piece in pieces)
            {
                builder.Append(piece);

                var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
                while (renderer.DocumentUpdate is not { } update || update.Version < builder.Version)
                {
                    if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("the producer never caught up");
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Thread.Sleep(0);
                }

                window.UpdateLayout();
            }

            Dispatcher.UIThread.RunJobs();
            splices = producer.SpliceCount;
            return Capture(renderer);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Renders the markdown by appending it a few characters at a time, as a host streaming would.</summary>
    private static byte[] RenderStreamed(string markdown)
    {
        var renderer = new MarkdownRenderer();
        var window = new Window { Width = Width, Height = Height, Background = Brushes.White, Content = renderer };
        try
        {
            window.Show();
            window.UpdateLayout();

            var source = new StringBuilder();
            MarkdownDocumentUpdate? previous = null;
            long version = 0;
            while (source.Length < markdown.Length)
            {
                var take = Math.Min(7, markdown.Length - source.Length);
                var startIndex = source.Length;
                source.Append(markdown, startIndex, take);

                var change = new ObservableStringBuilderChangedEventArgs(startIndex, take, source.Length, ++version);
                var document = Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline);
                MarkdownDocumentUpdate update = previous is null
                    ? new MarkdownDocumentUpdate.Full(document, change.Version)
                    : new MarkdownDocumentUpdate.Incremental(previous, document, change);

                renderer.DocumentUpdate = update;
                previous = update;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            return Capture(renderer);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The raw BGRA bytes of a rendered visual.</summary>
    private static byte[] Capture(Visual visual)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        bitmap.Render(visual);

        var pixels = new byte[Width * Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    /// <summary>Renders the markdown and returns the raw BGRA bytes.</summary>
    private static byte[] Render(string markdown, bool mergeRuns = true, int? linesPerChunk = null)
    {
        var previous = SyntaxHighlighting.MergeAdjacentRuns;
        var previousChunk = CodeBlock.LinesPerChunk;
        SyntaxHighlighting.MergeAdjacentRuns = mergeRuns;
        if (linesPerChunk is { } chunk) CodeBlock.LinesPerChunk = chunk;
        try
        {
            var renderer = new MarkdownRenderer
            {
                DocumentUpdate = new MarkdownDocumentUpdate.Full(
                    Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline)),
            };
            var window = new Window
            {
                Width = Width,
                Height = Height,
                Background = Brushes.White,
                Content = renderer,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                return Capture(renderer);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            SyntaxHighlighting.MergeAdjacentRuns = previous;
            CodeBlock.LinesPerChunk = previousChunk;
        }
    }
}
