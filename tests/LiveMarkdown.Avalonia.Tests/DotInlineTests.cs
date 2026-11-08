using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// <see cref="DotInline"/> — a status dot that is part of the text rather than a control beside it, so a dot
/// inside a <see cref="Link"/> is that link everywhere in the room it reserves: over the circle, in the gap
/// after it, and above and below it within the line.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DotInlineTests
{
    private const double FontSize = 16;

    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession() => session = HeadlessSession.Current;

    /// <summary>A laid-out block: "see " + Link[dot, "acme#13"] + " here".</summary>
    private static (MarkdownTextBlock Block, Link Link, DotInline Dot, Run Text, Window Window) Build()
    {
        var dot = new DotInline { Fill = Brushes.Green };
        var text = new Run("acme#13");
        var link = new Link { HRef = new Uri("https://example.com/acme/13") };
        link.Inlines.Add(dot);
        link.Inlines.Add(text);

        var block = new MarkdownTextBlock { FontSize = FontSize, FontFamily = FontFamily.Default };
        block.Inlines!.Add(new Run("see "));
        block.Inlines!.Add(link);
        block.Inlines!.Add(new Run(" here"));

        var window = new Window { Width = 600, Height = 300, Content = block };
        window.Show();
        return (block, link, dot, text, window);
    }

    /// <summary>The room the dot's joiner was given in the line. "see " is four characters.</summary>
    private static Rect DotRoom(MarkdownTextBlock block)
    {
        var bounds = block.TextLayout.HitTestTextRange(4, 1).ToList();
        Assert.That(bounds, Is.Not.Empty, "the dot must have been laid out for this test to mean anything");
        return bounds[0];
    }

    [Test]
    public void The_Dot_Reserves_Its_Size_And_Spacing() => session.Dispatch(() =>
    {
        var (block, _, dot, _, window) = Build();
        try
        {
            Assert.That(
                DotRoom(block).Width,
                Is.EqualTo(Math.Round(FontSize * (dot.Size + dot.Spacing))).Within(0.01),
                "the joiner is shaped to exactly the dot plus its gap, whatever the font draws for it");
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    [Test]
    public void The_Whole_Room_Is_The_Link() => session.Dispatch(() =>
    {
        var (block, link, dot, _, window) = Build();
        try
        {
            var room = DotRoom(block);
            var diameter = Math.Round(FontSize * dot.Size);

            Assert.Multiple(() =>
            {
                Assert.That(block.UpdatePointerOverLink(new Point(room.Left + diameter / 2, room.Center.Y)), Is.SameAs(link),
                    "on the circle");
                Assert.That(block.UpdatePointerOverLink(new Point(room.Right - 1, room.Center.Y)), Is.SameAs(link),
                    "in the gap between the circle and the text");
                Assert.That(block.UpdatePointerOverLink(new Point(room.Left + diameter / 2, room.Top + 1)), Is.SameAs(link),
                    "above the circle, still inside the line");
                Assert.That(block.UpdatePointerOverLink(new Point(room.Left - 3, room.Center.Y)), Is.Null,
                    "the prose before it is not the link");
            });
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    [Test]
    public void The_Joiner_Is_Not_Text() => session.Dispatch(() =>
    {
        var (block, _, _, _, window) = Build();
        try
        {
            block.SelectAll();
            Assert.Multiple(() =>
            {
                Assert.That(block.ActualText, Is.EqualTo("see acme#13 here"));
                Assert.That(block.ActualSelectedText, Is.EqualTo("see acme#13 here"), "a copied selection leaves it out");
            });
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    [Test]
    public void Recolouring_The_Dot_Leaves_The_Text_Where_It_Was() => session.Dispatch(() =>
    {
        var (block, _, dot, _, window) = Build();
        try
        {
            var before = block.TextLayout.HitTestTextRange(5, 7).First();
            var layout = block.TextLayout;

            dot.Fill = null;
            dot.Stroke = Brushes.Gray;
            window.UpdateLayout();

            Assert.Multiple(() =>
            {
                Assert.That(block.TextLayout, Is.SameAs(layout), "a new colour is a repaint, not a relayout");
                Assert.That(block.TextLayout.HitTestTextRange(5, 7).First(), Is.EqualTo(before));
            });
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    [Test]
    public void The_Dot_Is_Painted_At_The_Leading_Edge_Of_Its_Room() => session.Dispatch(() =>
    {
        var (block, _, dot, _, window) = Build();
        try
        {
            var room = DotRoom(block);
            var diameter = Math.Round(FontSize * dot.Size);
            var line = block.TextLayout.TextLines[0];
            var centre = new Point(room.Left + diameter / 2, line.Baseline - Math.Round(FontSize * dot.Elevation));

            var filled = PixelAt(block, centre);
            var gap = PixelAt(block, new Point(room.Right - 1, centre.Y));
            dot.Fill = null;
            dot.Stroke = Brushes.Green;
            var ringMiddle = PixelAt(block, centre);
            var ringEdge = PixelAt(block, new Point(room.Left + 0.5, centre.Y));

            Assert.Multiple(() =>
            {
                Assert.That(filled, Is.EqualTo((0, 128, 0)), "the fill is drawn on the dot's centre");
                Assert.That(gap, Is.Not.EqualTo((0, 128, 0)), "and not over the gap after it");
                Assert.That(ringMiddle, Is.Not.EqualTo((0, 128, 0)), "a stroke alone is a hollow ring");
                Assert.That(ringEdge.G, Is.GreaterThan(0), "whose edge is drawn");
            });
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>The (R, G, B) of one pixel of the block, rendered on its own.</summary>
    private static (int R, int G, int B) PixelAt(MarkdownTextBlock block, Point point)
    {
        var size = new PixelSize((int)Math.Ceiling(block.Bounds.Width), (int)Math.Ceiling(block.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(block);

        var pixel = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect((int)point.X, (int)point.Y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return (pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>The whole round trip through real input: the renderer's pointer handling, its hit test and its
    /// link click, at the circle, the gap after it and the space above it.</summary>
    [Test]
    public void A_Click_Anywhere_In_The_Room_Clicks_The_Link() => session.Dispatch(() =>
    {
        var dot = new DotInline { Stroke = Brushes.Gray };
        var link = new Link { HRef = new Uri("https://example.com/acme/13") };
        link.Inlines.Add(dot);
        link.Inlines.Add(new Run("acme#13"));
        var other = new Link { HRef = new Uri("https://example.com/other") };
        other.Inlines.Add(new Run("other"));
        var block = new MarkdownTextBlock { FontSize = FontSize, FontFamily = FontFamily.Default };
        block.Inlines!.Add(new Run("see "));
        block.Inlines!.Add(link);
        block.Inlines!.Add(new Run(" or "));
        block.Inlines!.Add(other);

        var renderer = new BlockRenderer(block);
        var clicked = new List<Uri?>();
        renderer.LinkClick += (_, e) => clicked.Add(e.HRef);
        var hand = new List<bool>();
        var window = new Window { Width = 600, Height = 200, Content = renderer };
        try
        {
            window.Show();
            var room = DotRoom(block);
            var diameter = Math.Round(FontSize * dot.Size);
            foreach (var point in new[]
                     {
                         new Point(room.Left + diameter / 2, room.Center.Y),
                         new Point(room.Right - 1, room.Center.Y),
                         new Point(room.Left + diameter / 2, room.Top + 1),
                     })
            {
                var target = block.TranslatePoint(point, window)!.Value;
                window.MouseMove(target);
                hand.Add(block.Classes.Contains(":pointerover-link"));
                window.MouseDown(target, MouseButton.Left);
                window.MouseUp(target, MouseButton.Left);
            }

            Assert.Multiple(() =>
            {
                Assert.That(hand, Is.EqualTo(new[] { true, true, true }), "the hand, everywhere in the room");
                Assert.That(clicked, Has.Count.EqualTo(3).And.All.EqualTo(link.HRef), "and the link's click");
                Assert.That(link.IsClicked, Is.True, "the link holding the dot is the one marked visited");
                Assert.That(other.IsClicked, Is.False, "and no other link in the block is");
            });
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();

    private sealed class BlockRenderer : MarkdownRenderer
    {
        public BlockRenderer(MarkdownTextBlock block) => ((Panel)VisualChildren[0]).Children.Add(block);

        protected override Type StyleKeyOverride => typeof(MarkdownRenderer);
    }

    [Test]
    public void The_Link_Tip_Is_The_Blocks_While_Over_It() => session.Dispatch(() =>
    {
        var (block, link, dot, _, window) = Build();
        try
        {
            link.Tip = "Open";
            var room = DotRoom(block);

            block.UpdatePointerOverLink(new Point(room.Left + 2, room.Center.Y));
            var over = ToolTip.GetTip(block);
            block.UpdatePointerOverLink(new Point(room.Left - 3, room.Center.Y));
            var off = ToolTip.GetTip(block);

            Assert.Multiple(() =>
            {
                Assert.That(over, Is.EqualTo("Open"));
                Assert.That(off, Is.Null);
            });
        }
        finally
        {
            window.Close();
        }
    }, CancellationToken.None).GetAwaiter().GetResult();
}
