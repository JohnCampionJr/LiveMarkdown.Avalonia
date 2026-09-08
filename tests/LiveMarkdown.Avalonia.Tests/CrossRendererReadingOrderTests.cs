using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// A selection scope that spans renderers reads them in the order they are laid out, not the order they
/// were added. A virtualizing host appends and recycles containers, so its child order is arbitrary; the
/// range a drag covers and the text a copy yields must still follow the screen.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CrossRendererReadingOrderTests
{
    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession() => session = HeadlessSession.Current;

    private static MarkdownRenderer Renderer(string text, double top)
    {
        var r = new MarkdownRenderer { MarkdownBuilder = new ObservableStringBuilder(text), Width = 600 };
        Canvas.SetLeft(r, 0);
        Canvas.SetTop(r, top);
        return r;
    }

    private static MarkdownTextBlock Block(MarkdownRenderer r) =>
        r.GetVisualDescendants().OfType<MarkdownTextBlock>().First(b => b.Bounds.Height > 0);

    private static Point At(Window w, MarkdownTextBlock b, double x) =>
        b.TranslatePoint(new Point(x, b.Bounds.Height / 2), w) ?? default;

    [Test]
    public void A_Drag_Across_Renderers_Reads_Top_To_Bottom_Whatever_The_Child_Order() => session.Dispatch(() =>
    {
        // The LOWER renderer is added first, so tree order is the reverse of layout order.
        var lower = Renderer("second row", 100);
        var upper = Renderer("first row", 0);
        var canvas = new Canvas { Width = 600, Height = 300 };
        canvas.Children.Add(lower);
        canvas.Children.Add(upper);
        MarkdownTextBlock.SetIsSelectionScope(canvas, true);

        var window = new Window { Width = 600, Height = 300, Content = canvas };
        window.Show();
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            if (upper.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(b => b.Bounds.Height > 0)
                && lower.GetVisualDescendants().OfType<MarkdownTextBlock>().Any(b => b.Bounds.Height > 0)) break;
            Thread.Sleep(5);
        }
        window.UpdateLayout();

        var from = At(window, Block(upper), 1);
        var to = At(window, Block(lower), 590);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 30, from.Y));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.That(upper.SelectedText, Is.EqualTo("first row" + Environment.NewLine + "second row"),
            "the selection covers both, and reads top to bottom");
        Assert.That(lower.SelectedText, Is.EqualTo(upper.SelectedText), "one scope, one selection");
    }, CancellationToken.None).GetAwaiter().GetResult();
}
