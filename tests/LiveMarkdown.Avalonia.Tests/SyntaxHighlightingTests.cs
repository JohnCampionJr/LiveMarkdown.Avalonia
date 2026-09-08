using Avalonia.Controls.Documents;
using Avalonia.Media;
using NUnit.Framework;
using TextMateSharp.Themes;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class SyntaxHighlightingTests
{
    [Test]
    public void ColorTheme_RemainsTheFallbackWhenCustomThemeNameIsNotSet()
    {
        var customTheme = new TestRawTheme("#123456");
        const string themeName = "LiveMarkdown.Tests.Fallback";

        try
        {
            SyntaxHighlighting.RegisterCustomTheme(themeName, customTheme);

            var codeBlock = CreateCodeBlock(null);

            Assert.That(GetForeground(codeBlock, "public"), Is.Not.EqualTo(Color.Parse("#123456")));
        }
        finally
        {
            SyntaxHighlighting.UnregisterCustomTheme(themeName);
        }
    }

    [Test]
    public void CustomThemeName_UsesRegisteredThemeAndRehighlightsWhenChanged()
    {
        const string firstThemeName = "LiveMarkdown.Tests.First";
        const string secondThemeName = "LiveMarkdown.Tests.Second";

        try
        {
            SyntaxHighlighting.RegisterCustomTheme(firstThemeName, new TestRawTheme("#123456"));
            SyntaxHighlighting.RegisterCustomTheme(secondThemeName, new TestRawTheme("#654321"));

            var codeBlock = CreateCodeBlock(firstThemeName);
            Assert.That(GetForeground(codeBlock, "public"), Is.EqualTo(Color.Parse("#123456")));

            codeBlock.CustomColorTheme = secondThemeName;

            Assert.That(GetForeground(codeBlock, "public"), Is.EqualTo(Color.Parse("#654321")));
        }
        finally
        {
            SyntaxHighlighting.UnregisterCustomTheme(firstThemeName);
            SyntaxHighlighting.UnregisterCustomTheme(secondThemeName);
        }
    }

    [Test]
    public void MissingCustomThemeName_FallsBackToColorTheme()
    {
        var customTheme = new TestRawTheme("#123456");
        const string themeName = "LiveMarkdown.Tests.Missing";

        try
        {
            SyntaxHighlighting.RegisterCustomTheme(themeName, customTheme);

            var codeBlock = CreateCodeBlock("LiveMarkdown.Tests.NotRegistered");

            Assert.That(GetForeground(codeBlock, "public"), Is.Not.EqualTo(Color.Parse("#123456")));
        }
        finally
        {
            SyntaxHighlighting.UnregisterCustomTheme(themeName);
        }
    }

    [Test]
    public void RegisteringTheSameNameReplacesTheCachedThemeAndParsesOnce()
    {
        const string themeName = "LiveMarkdown.Tests.Replaced";
        var firstTheme = new TestRawTheme("#123456");
        var secondTheme = new TestRawTheme("#654321");

        try
        {
            SyntaxHighlighting.RegisterCustomTheme(themeName, firstTheme);

            var firstBlock = CreateCodeBlock(themeName);
            var secondBlock = CreateCodeBlock(themeName);

            Assert.That(GetForeground(firstBlock, "public"), Is.EqualTo(Color.Parse("#123456")));
            Assert.That(GetForeground(secondBlock, "public"), Is.EqualTo(Color.Parse("#123456")));
            Assert.That(firstTheme.TokenColorsReadCount, Is.EqualTo(1));

            SyntaxHighlighting.RegisterCustomTheme(themeName, secondTheme);
            var replacedBlock = CreateCodeBlock(themeName);

            Assert.That(GetForeground(replacedBlock, "public"), Is.EqualTo(Color.Parse("#654321")));
            Assert.That(secondTheme.TokenColorsReadCount, Is.EqualTo(1));
        }
        finally
        {
            SyntaxHighlighting.UnregisterCustomTheme(themeName);
        }
    }

    // ---------------------------------------------------------------- run merging

    [Test]
    public void AdjacentRunsOfALineNeverShareTheSameStyle()
    {
        // The invariant behind emitting one Run per styled group rather than one per token: if two
        // neighbours agree on everything the theme sets, the boundary between them is invisible and
        // the second run is a shaped run bought for nothing on every measure of the block.
        var codeBlock = new CodeBlock
        {
            Language = "csharp",
            Code = "public static int Add(int first, int second) => first + second;",
        };

        foreach (var line in codeBlock.Inlines.OfType<Span>())
        {
            var runs = line.Inlines.OfType<Run>().ToList();
            Assert.That(runs, Has.Count.GreaterThan(1), "sanity: this line has more than one style");

            for (var i = 1; i < runs.Count; i++)
            {
                var previous = runs[i - 1];
                var current = runs[i];
                var same =
                    Equals((previous.Foreground as ISolidColorBrush)?.Color, (current.Foreground as ISolidColorBrush)?.Color) &&
                    Equals((previous.Background as ISolidColorBrush)?.Color, (current.Background as ISolidColorBrush)?.Color) &&
                    previous.FontStyle == current.FontStyle &&
                    previous.FontWeight == current.FontWeight;

                Assert.That(
                    same,
                    Is.False,
                    $"runs '{previous.Text}' and '{current.Text}' look identical and should have been one run");
            }
        }
    }

    [Test]
    public void MergingRunsKeepsTheLineTextExactly()
    {
        const string code = "public static int Add(int first, int second) => first + second;";
        var codeBlock = new CodeBlock { Language = "csharp", Code = code };

        var rendered = string.Concat(EnumerateRuns(codeBlock.Inlines).Select(run => run.Text));

        Assert.That(rendered, Is.EqualTo(code), "nothing dropped, duplicated or reordered by merging");
    }

    [Test]
    public void AMultiLineBlockKeepsOneInlinePerLine()
    {
        // Merging happens WITHIN a line. It must never reach across the line break, which is a
        // separate inline and the thing that puts the next line on its own row.
        var codeBlock = new CodeBlock
        {
            Language = "csharp",
            Code = "var first = 1;\nvar second = 2;\nvar third = 3;",
        };

        var lines = codeBlock.Inlines.Count(inline => inline is Run or Span);
        var breaks = codeBlock.Inlines.Count(inline => inline is LineBreak);

        Assert.That(lines, Is.EqualTo(3));
        Assert.That(breaks, Is.EqualTo(2));
    }

    private static CodeBlock CreateCodeBlock(string? customThemeName)
    {
        var codeBlock = new CodeBlock
        {
            Language = "csharp",
            CustomColorTheme = customThemeName,
            Code = "public class Demo {}"
        };

        return codeBlock;
    }

    private static Color? GetForeground(CodeBlock codeBlock, string text)
    {
        foreach (var run in EnumerateRuns(codeBlock.Inlines))
        {
            if (run.Text != text) continue;
            return (run.Foreground as ISolidColorBrush)?.Color;
        }

        Assert.Fail($"The highlighted code did not contain a run with text '{text}'.");
        return null;
    }

    private static IEnumerable<Run> EnumerateRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (var nestedRun in EnumerateRuns(span.Inlines))
                    yield return nestedRun;
            }
        }
    }

    private sealed class TestRawTheme : IRawTheme
    {
        private readonly string _foreground;

        public TestRawTheme(string foreground)
        {
            _foreground = foreground;
        }

        public int TokenColorsReadCount { get; private set; }

        public string GetName() => "LiveMarkdown test theme";

        public string GetInclude() => string.Empty;

        public ICollection<IRawThemeSetting> GetSettings() => [];

        public ICollection<IRawThemeSetting> GetTokenColors()
        {
            TokenColorsReadCount++;
            return [new TestRawThemeSetting(_foreground)];
        }

        public ICollection<KeyValuePair<string, object>> GetGuiColors() => [];
    }

    private sealed class TestRawThemeSetting : IRawThemeSetting
    {
        private readonly string _foreground;

        public TestRawThemeSetting(string foreground)
        {
            _foreground = foreground;
        }

        public string GetName() => "keyword";

        public object GetScope() => "storage.modifier.public.cs";

        public IThemeSetting GetSetting() => new TestThemeSetting(_foreground);
    }

    private sealed class TestThemeSetting : IThemeSetting
    {
        private readonly string _foreground;

        public TestThemeSetting(string foreground)
        {
            _foreground = foreground;
        }

        public object GetFontStyle() => string.Empty;

        public string GetBackground() => string.Empty;

        public string GetForeground() => _foreground;
    }
}
