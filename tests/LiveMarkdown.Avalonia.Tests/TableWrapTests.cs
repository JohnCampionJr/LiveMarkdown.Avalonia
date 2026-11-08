using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// A table wider than its space narrows its long columns and wraps their cells, rather than running off the
/// edge behind a horizontal scrollbar.
/// </summary>
[TestFixture]
[NonParallelizable]
public class TableWrapTests
{
    private const string LongTable =
        """
        | Problem | Details |
        |---|---|
        | Chromium 120 | The latest package is still on an old engine, a branch for the newer one has sat unmerged for months, and nobody has said when it will land or whether it will land at all. |
        | Crash on exit | Short |
        """;

    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession()
    {
        session = HeadlessSession.Current;
    }

    /// <summary>A dispatch that cannot finish in this long has found the layout looping; fail rather than hang
    /// the run. (It cannot catch a DEAD session: the session only registers this token once it dequeues the job.)</summary>
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Test]
    public async Task LongTable_FitsTheWindow_AndWrapsTheLongCell()
    {
        var result = await session.Dispatch(
            () =>
            {
                var (window, renderer) = Show(LongTable, width: 500);
                try
                {
                    return Measure(renderer);
                }
                finally
                {
                    window.Close();
                }
            },
            Timeout());

        Assert.Multiple(() =>
        {
            Assert.That(result.Extent, Is.LessThanOrEqualTo(result.Viewport + 0.5), "the table scrolls instead of wrapping");
            Assert.That(result.LongCellHeight, Is.GreaterThan(result.ShortCellHeight * 2), "the long cell did not wrap");
        });
    }

    [Test]
    public async Task NarrowTable_KeepsItsNaturalWidths()
    {
        var result = await session.Dispatch(
            () =>
            {
                var (window, renderer) = Show("| A | B |\n|---|---|\n| one | two |\n", width: 500);
                try
                {
                    var cells = renderer.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("TableCell")).ToList();
                    return cells.Select(c => (c.Bounds.Width, c.DesiredSize.Width)).ToList();
                }
                finally
                {
                    window.Close();
                }
            },
            Timeout());

        Assert.That(result, Is.Not.Empty);
        Assert.That(result.Select(r => r.Item1), Is.All.LessThan(100), "a short table was stretched");
    }

    [Test]
    public async Task ShrinkingTheWindow_RewrapsTheTable()
    {
        var result = await session.Dispatch(
            () =>
            {
                var (window, renderer) = Show(LongTable, width: 3000);
                try
                {
                    var wide = Measure(renderer);
                    window.Width = 500;
                    Settle(window);
                    return (Wide: wide, Narrow: Measure(renderer));
                }
                finally
                {
                    window.Close();
                }
            },
            Timeout());

        Assert.Multiple(() =>
        {
            Assert.That(result.Wide.LongCellHeight, Is.LessThan(result.Wide.ShortCellHeight * 1.5), "the wide window already wrapped");
            Assert.That(result.Narrow.Extent, Is.LessThanOrEqualTo(result.Narrow.Viewport + 0.5), "a resize left the table scrolling");
            Assert.That(result.Narrow.LongCellHeight, Is.GreaterThan(result.Narrow.ShortCellHeight * 2), "a resize did not rewrap");
        });
    }

    private const string ColorTable =
        """
        | Token | Dark | Light | HeatherV2 | HeatherLight |
        |---|---|---|---|---|
        | Text.Secondary, the quieter tone for supporting text beside a heading | `#6b6357` | `#6c6678` | `#2b2722` | `#2a2630` |
        | Accent | `#e08a64` | `#e08a64` | `#bb6a4a` | `#bb6a4a` |
        | Thread.Moss | `#9aab63` | `#9aab63` | `#6f7a45` | `#6f7a45` |
        """;

    // The first column is prose that can wrap; every other cell is one word that can't.
    private static readonly string[] Words =
        ["Token", "Dark", "Light", "HeatherV2", "HeatherLight", "Accent", "Thread.Moss", "#6b6357", "#6c6678",
         "#2b2722", "#2a2630", "#e08a64", "#bb6a4a", "#9aab63", "#6f7a45"];

    /// <summary>
    /// A column is never narrowed below its longest word: a cell may wrap BETWEEN words, never inside one. The
    /// table that found this had five columns of hex colours squeezed to the 80px floor, which split every
    /// <c>#6b6357</c> into <c>#6b635</c> / <c>7</c>. Wide enough for every column's longest word, the table
    /// must still fit without scrolling.
    /// </summary>
    [Test]
    public async Task NarrowedColumns_NeverBreakAWord()
    {
        var result = await session.Dispatch(() => ShowColorTable(width: 560), Timeout());

        Assert.Multiple(() =>
        {
            Assert.That(result.Narrowed, Is.True, "the table has to be narrower than its natural width to test anything");
            Assert.That(result.Broken, Is.Empty, "a cell broke inside a word");
            Assert.That(result.ProseWrapped, Is.True, "the prose column should have taken the narrowing");
            Assert.That(result.Extent, Is.LessThanOrEqualTo(result.Viewport + 0.5), "the table scrolls although every word fits");
        });
    }

    /// <summary>Too narrow for its words: every one is under <see cref="MarkdownTableGrid.MaxUnbreakableWidth"/>, so
    /// the table keeps them all whole and scrolls rather than breaking them — even though each column's even share
    /// of the width is narrower than its words. (Found at a 360px transcript: four columns of swatched hex values
    /// each lost their last character while an even-share cap held the table to the viewport.)</summary>
    [Test]
    public async Task TooNarrowForItsWords_ScrollsRatherThanBreakingThem()
    {
        var result = await session.Dispatch(() => ShowColorTable(width: 260), Timeout());

        Assert.Multiple(() =>
        {
            Assert.That(result.Broken, Is.Empty, "a short word broke inside itself");
            Assert.That(result.Extent, Is.GreaterThan(result.Viewport), "the table should scroll when its floors cannot fit");
        });
    }

    /// <summary>
    /// One very long unbreakable token must not hold its column open. Found in a mutation-test table: a
    /// 60-character test name in the second column floored that column at its full length, squeezed the first
    /// column of prose to <see cref="MarkdownTableGrid.MinColumnWidth"/> (one word per line), and pushed the
    /// rest of the second column off the edge. A token wider than an even share breaks instead, as it did before
    /// columns had word floors at all.
    /// </summary>
    [Test]
    public async Task ALongIdentifier_DoesNotSqueezeTheOtherColumns()
    {
        const string table =
            """
            | Mutant | Caught by |
            |---|---|
            | A: an error result doesn't claim its event | the "error result" and "gate refuses" cases. The guard now sits outside the gate |
            | D: a late event isn't absorbed by its timed-out request | `On_A_Backend_That_Reports_Calls_A_Late_Event_Is_Absorbed_By_Its_Timed_Out_Request` |
            """;

        var result = await session.Dispatch(
            () =>
            {
                var (window, renderer) = Show(table, width: 600);
                try
                {
                    var scroller = renderer.GetVisualDescendants().OfType<MarkdownTableScrollViewer>().Single();
                    var cells = renderer.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("TableCell")).ToList();
                    var firstColumn = cells.Where(c => Grid.GetColumn(c) == 0).Max(c => c.Bounds.Width);
                    return (scroller.Extent.Width, scroller.Viewport.Width, FirstColumn: firstColumn);
                }
                finally
                {
                    window.Close();
                }
            },
            Timeout());

        Assert.Multiple(() =>
        {
            Assert.That(result.Item1, Is.LessThanOrEqualTo(result.Item2 + 0.5), "the long identifier pushed the table off the edge");
            Assert.That(result.FirstColumn, Is.GreaterThan(150), "the prose column was squeezed to the minimum");
        });
    }

    /// <summary>
    /// An embedded control glued to a word — a colour swatch in front of its hex chip — lays out as one unit with
    /// it, so it is part of that word's width. Measured against the logical text (where the control has no
    /// position) or with UAX #14's break beside it, the swatch fell out of the measure and every hex value in a
    /// fitted table lost its last character to the next line.
    /// </summary>
    [Test]
    public async Task AnEmbeddedControl_CountsTowardsTheWordItTouches()
    {
        var result = await session.Dispatch(
            () =>
            {
                const double swatch = 20;
                var glued = new MarkdownTextBlock { FontSize = 14, Inlines = [] };
                glued.Inlines!.Add(new InlineUIContainer(new Border { Width = swatch, Height = 10 }));
                glued.Inlines.Add(new Run("#3A7BD5"));

                var bare = new MarkdownTextBlock { FontSize = 14, Text = "#3A7BD5" };

                var spaced = new MarkdownTextBlock { FontSize = 14, Inlines = [] };
                spaced.Inlines!.Add(new Run("#3A7BD5 "));
                spaced.Inlines.Add(new InlineUIContainer(new Border { Width = swatch, Height = 10 }));

                foreach (var b in new[] { glued, bare, spaced }) b.Measure(Size.Infinity);
                return (Glued: glued.MeasureLongestWord(), Bare: bare.MeasureLongestWord(), Spaced: spaced.MeasureLongestWord(), Swatch: swatch);
            },
            Timeout());

        Assert.Multiple(() =>
        {
            Assert.That(result.Glued, Is.GreaterThanOrEqualTo(result.Bare + result.Swatch - 1), "the swatch fell out of its word's width");
            Assert.That(result.Spaced, Is.LessThanOrEqualTo(result.Bare + 1), "a control after a space is its own word, not part of the one before");
        });
    }

    private static (bool Narrowed, double Extent, double Viewport, List<string> Broken, bool ProseWrapped) ShowColorTable(double width)
    {
        var (wideWindow, wideRenderer) = Show(ColorTable, width: 3000);
        double natural;
        try
        {
            natural = wideRenderer.GetVisualDescendants().OfType<MarkdownTableGrid>().Single().DesiredSize.Width;
        }
        finally
        {
            wideWindow.Close();
        }

        var (window, renderer) = Show(ColorTable, width);
        try
        {
            var scroller = renderer.GetVisualDescendants().OfType<MarkdownTableScrollViewer>().Single();
            var grid = renderer.GetVisualDescendants().OfType<MarkdownTableGrid>().Single();
            var blocks = renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().ToList();
            var broken = blocks.Where(b => Words.Contains(b.ActualText) && b.TextLayout.TextLines.Count > 1)
                .Select(b => $"{b.ActualText} {b.TextWrapping} {b.Bounds.Width:F1}/{b.DesiredSize.Width:F1}").ToList();
            var proseWrapped = blocks.Single(b => b.ActualText.StartsWith("Text.Secondary")).TextLayout.TextLines.Count > 1;
            return (grid.DesiredSize.Width < natural - 0.5, scroller.Extent.Width, scroller.Viewport.Width, broken, proseWrapped);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window, MarkdownRenderer) Show(string markdown, double width)
    {
        var renderer = new MarkdownRenderer
        {
            DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse(markdown, MarkdownUpdateProducer.DefaultPipeline)),
        };
        var window = new Window
        {
            Width = width,
            Height = 800,
            Content = renderer,
            Styles = { ScrollingTemplate() },
        };
        window.Show();
        Settle(window);
        return (window, renderer);
    }

    /// <summary>
    /// The test application has no theme, so a <see cref="ScrollViewer"/> there has no template and simply hands
    /// its content its own width. A real theme puts a <see cref="ScrollContentPresenter"/> in between, which
    /// measures horizontally scrollable content at infinite width — the case the table has to wrap through.
    /// </summary>
    private static Style ScrollingTemplate() => new(x => x.OfType<ScrollViewer>())
    {
        Setters =
        {
            new Setter(
                TemplatedControl.TemplateProperty,
                new FuncControlTemplate<ScrollViewer>((viewer, scope) => new ScrollContentPresenter
                {
                    Name = "PART_ContentPresenter",
                    CanHorizontallyScroll = true,
                    [!ContentPresenter.ContentProperty] = viewer[!ContentControl.ContentProperty],
                }.RegisterInNameScope(scope))),
        },
    };

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static (double Extent, double Viewport, double LongCellHeight, double ShortCellHeight) Measure(MarkdownRenderer renderer)
    {
        var scroller = renderer.GetVisualDescendants().OfType<MarkdownTableScrollViewer>().Single();
        var cells = renderer.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("TableCell")).ToList();
        var longCell = cells.Single(c => c.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(t => t.ActualText.StartsWith("The latest")));
        var shortCell = cells.First(c => c.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(t => t.ActualText == "Short"));
        return (scroller.Extent.Width, scroller.Viewport.Width, longCell.Bounds.Height, shortCell.Bounds.Height);
    }
}
