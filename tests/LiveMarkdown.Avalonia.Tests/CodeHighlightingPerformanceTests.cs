using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using NUnit.Framework;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Themes;
using Avalonia.Media;
using FontStyle = Avalonia.Media.FontStyle;
using System.Collections;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class CodeHighlightingPerformanceTests
{
    [Test]
    public Task Identical_code_assignment_preserves_styled_inlines_and_text_layout() => OnSession(() =>
    {
        const string source = "public static string Message(int value) => $\"value {value}\";";
        var block = new CodeBlock { Language = "csharp", Code = source };
        var window = new Window { Width = 500, Height = 200, Content = block };
        try
        {
            window.Show();
            window.UpdateLayout();
            var inline = block.Inlines[0];
            var layout = block.CodeTextBlock!.TextLayout;
            Assert.That(inline, Is.TypeOf<Span>());
            block.Code = source;
            window.UpdateLayout();
            Assert.That(block.Inlines[0], Is.SameAs(inline));
            Assert.That(block.CodeTextBlock.TextLayout, Is.SameAs(layout));
            block.SetLine(0, source + " // changed");
            block.HighlightSyntax();
            Assert.That(block.Code, Is.EqualTo(source + " // changed"));
            Assert.That(block.Inlines[0], Is.Not.SameAs(inline));
        }
        finally { window.Close(); }
    });

    [Test]
    public Task Theme_changes_restyle_an_unchanged_line_and_external_edits_are_not_hidden() => OnSession(() =>
    {
        const string source = "public class Example {}";
        var block = new CodeBlock { Language = "csharp", Code = source };
        var previous = block.Inlines[0];
        block.ColorTheme = ThemeName.LightPlus;
        Assert.That(block.Inlines[0], Is.Not.SameAs(previous));
        Assert.That(block.Code, Is.EqualTo(source));
        block.Inlines[0] = new Run("externally changed");
        block.SetLine(0, source);
        block.HighlightSyntax();
        Assert.That(block.Code, Is.EqualTo(source));
    });

    [Test]
    public Task Replacing_a_theme_and_reassigning_identical_code_removes_obsolete_single_run_styles() => OnSession(() =>
    {
        var name = "LiveMarkdown.Tests.SingleRunReplacement." + Guid.NewGuid();
        try
        {
            SyntaxHighlighting.RegisterCustomTheme(name, new SingleRunTheme("italic", "#456789"));
            var block = new CodeBlock { Language = "csharp", CustomColorTheme = name, Code = "public" };
            var oldRun = (Run)block.Inlines[0];
            Assert.That(oldRun.FontStyle, Is.EqualTo(FontStyle.Italic));
            Assert.That(oldRun.Background, Is.Not.Null);

            SyntaxHighlighting.RegisterCustomTheme(name, new SingleRunTheme("", ""));
            block.Code = "public";
            var replacement = (Run)block.Inlines[0];
            Assert.That(replacement, Is.Not.SameAs(oldRun));
            Assert.That(replacement.FontStyle, Is.EqualTo(FontStyle.Normal));
            Assert.That(replacement.Background, Is.Null);
            Assert.That(block.Code, Is.EqualTo("public"));
        }
        finally { SyntaxHighlighting.UnregisterCustomTheme(name); }
    });

    [Test]
    public Task Changing_a_previously_styled_run_drops_styles_not_in_the_new_token_scope() => OnSession(() =>
    {
        var name = "LiveMarkdown.Tests.SingleRunTextChange." + Guid.NewGuid();
        try
        {
            SyntaxHighlighting.RegisterCustomTheme(name, new SingleRunTheme("italic", "#456789"));
            var block = new CodeBlock { Language = "csharp", CustomColorTheme = name, Code = "public" };
            var oldRun = (Run)block.Inlines[0];
            Assert.That(oldRun.FontStyle, Is.EqualTo(FontStyle.Italic));
            block.Code = "// comment";
            var replacement = (Run)block.Inlines[0];
            Assert.That(replacement, Is.Not.SameAs(oldRun));
            Assert.That(replacement.FontStyle, Is.EqualTo(FontStyle.Normal));
            Assert.That(replacement.Background, Is.Null);
            Assert.That(block.Code, Is.EqualTo("// comment"));
        }
        finally { SyntaxHighlighting.UnregisterCustomTheme(name); }
    });

    [Test]
    public Task Fully_reused_tokens_do_not_acquire_the_grammar_gate() => OnSession(() =>
    {
        var highlighter = new SyntaxHighlighting("csharp");
        var inlines = new InlineCollection { new Run("// cached gate check " + Guid.NewGuid()) };
        highlighter.FormatInlines(inlines);
        var before = highlighter.GrammarLockAcquisitions;
        highlighter.FormatInlines(inlines);
        Assert.That(highlighter.GrammarLockAcquisitions, Is.EqualTo(before));
    });

    [Test]
    public void A_background_warm_releases_the_gate_between_each_line()
    {
        var highlighter = new SyntaxHighlighting("csharp");
        var observed = new List<int>();
        highlighter.LineTokenizedObserver = line =>
        {
            Assert.That(highlighter.IsGrammarLockHeld, Is.False,
                "the per-line publication point must be outside the shared grammar critical section");
            observed.Add(line);
        };
        highlighter.Prewarm("// warm gate check " + Guid.NewGuid() + "\n// second line\n// third line");
        Assert.That(observed, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public async Task Foreground_tokenization_can_run_at_a_controlled_background_line_boundary()
    {
        var highlighter = new SyntaxHighlighting("csharp");
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var notifications = 0;
        highlighter.LineTokenizedObserver = _ =>
        {
            if (Interlocked.Increment(ref notifications) != 1) return;
            if (highlighter.IsGrammarLockHeld)
            {
                firstLine.TrySetException(new InvalidOperationException("background publication retained the grammar gate"));
                return;
            }
            firstLine.TrySetResult();
            resume.Wait();
        };
        var warm = Task.Run(() => highlighter.Prewarm("// background " + Guid.NewGuid() + "\n// remaining line"));
        try
        {
            await Task.WhenAny(firstLine.Task, warm);
            if (warm.IsCompleted)
            {
                await warm;
                Assert.That(firstLine.Task.IsCompleted, Is.True, "the warm completed without publishing its first-line signal");
            }
            await firstLine.Task;
            await OnSession(() =>
            {
                var inlines = new InlineCollection { new Run("// foreground " + Guid.NewGuid()) };
                highlighter.FormatInlines(inlines);
                Assert.That(SyntaxHighlighting.IsRunFormatted((Run)inlines[0]), Is.True);
            });
        }
        finally { resume.Set(); await warm; }
    }

    [TestCase(32)]
    [TestCase(512)]
    public Task Append_only_highlighting_reads_and_formats_only_the_dirty_tail(int count) => OnSession(() =>
    {
        var block = new CodeBlock { AutoSyntaxHighlight = false };
        for (var i = 0; i < count; i++) block.SetLine(i, $"// prefix {i}");
        var highlighter = new SyntaxHighlighting("csharp");
        var chunks = block.CodeTextBlocks.Select(text => text.Inlines!).ToArray();
        var lines = new CountedLines(block.LineTexts);
        highlighter.FormatInlines(chunks, knownLineTexts: lines, firstDirtyLine: 0, fixedLinesPerChunk: CodeBlock.LinesPerChunk);
        block.SetLine(count, "// tail");
        chunks = block.CodeTextBlocks.Select(text => text.Inlines!).ToArray();
        lines.Reads = 0;
        highlighter.FormatInlines(chunks, knownLineTexts: lines, firstDirtyLine: count, fixedLinesPerChunk: CodeBlock.LinesPerChunk);
        Assert.That(lines.Reads, Is.EqualTo(1), "the authoritative unchanged prefix is not rescanned");
        Assert.That(highlighter.LastFormattingLineVisits, Is.EqualTo(1));
    });

    [Test]
    public Task CodeBlock_propagates_dirtiness_through_append_trim_and_state_changes() => OnSession(() =>
    {
        var block = new CodeBlock { AutoSyntaxHighlight = false, Language = "csharp" };
        for (var i = 0; i < 64; i++) block.SetLine(i, $"// prefix {i}");
        block.HighlightSyntax();
        block.SetLine(64, "/* new multiline comment");
        block.HighlightSyntax();
        Assert.That(block.LastHighlightLineVisits, Is.EqualTo(1));
        block.SetLine(65, "continued comment */");
        block.HighlightSyntax();
        Assert.That(block.LastHighlightLineVisits, Is.EqualTo(1));
        block.TrimLines(64);
        block.HighlightSyntax();
        Assert.That(block.LastHighlightLineVisits, Is.EqualTo(0));
        block.SetLine(64, "var value = 1;");
        block.HighlightSyntax();
        Assert.That(block.LastHighlightLineVisits, Is.EqualTo(1));
        Assert.That(block.Code, Does.EndWith("var value = 1;"));
    });

    [Test]
    public Task External_inline_text_and_collection_changes_use_the_safe_comparison_path() => OnSession(() =>
    {
        var block = new CodeBlock { Language = "csharp", Code = "// original" };
        var exposed = block.Inlines;
        ((Run)exposed[0]).Text = "public class Changed {}";
        block.HighlightSyntax();
        Assert.That(exposed[0], Is.TypeOf<Span>());
        Assert.That(block.Code, Is.EqualTo("public class Changed {}"));
        block.AutoSyntaxHighlight = false;
        block.CodeTextBlocks[0].Inlines = new InlineCollection { new Run("var result = 7;") };
        block.AutoSyntaxHighlight = true;
        Assert.That(block.Code, Is.EqualTo("var result = 7;"));
        Assert.That(block.CodeTextBlocks[0].Inlines![0], Is.TypeOf<Span>());
    });

    [Test]
    public Task Code_assignment_trims_externally_added_lines_and_clear_removes_an_external_only_body() => OnSession(() =>
    {
        var block = new CodeBlock { AutoSyntaxHighlight = false, Code = "first" };
        block.Inlines.Add(new LineBreak());
        block.Inlines.Add(new Run("external second"));
        block.Code = "first";
        Assert.That(block.Code, Is.EqualTo("first"));
        Assert.That(block.Inlines, Has.Count.EqualTo(1));

        var externalOnly = new CodeBlock { AutoSyntaxHighlight = false };
        externalOnly.Inlines.Add(new Run("external-only body"));
        externalOnly.Code = null;
        Assert.That(externalOnly.Code, Is.Null);
        Assert.That(externalOnly.CodeTextBlocks, Is.Empty);
    });

    [TestCase(false)]
    [TestCase(true)]
    public Task Appending_offscreen_code_checks_only_the_changed_deferred_chunk(bool wrap) => OnSession(() =>
    {
        var block = new CodeBlock
        {
            AutoSyntaxHighlight = false, Language = "csharp", DeferOffscreenChunkStyling = true, IsCodeWrapped = wrap,
        };
        for (var i = 0; i < 128; i++) block.SetLine(i, $"var value{i} = Compute({i});");
        var window = new Window { Width = 160, Height = 120, Content = new ScrollViewer { Content = block } };
        try
        {
            window.Show();
            window.UpdateLayout();
            block.HighlightSyntax();
            Assert.That(block.DeferredChunkCount, Is.GreaterThan(4), "the probe needs a genuinely offscreen prefix");
            block.SetLine(128, "var appended = Compute(128);");
            block.HighlightSyntax();
            Assert.That(block.LastHighlightLineVisits, Is.EqualTo(1));
            Assert.That(block.LastDeferredChunkVisits, Is.EqualTo(1));
            block.HighlightSyntax();
            Assert.That(block.LastDeferredChunkVisits, Is.Zero, "an unchanged highlight does not recheck offscreen chunks");
        }
        finally { window.Close(); }
    });

    [Test]
    public void Token_cache_preserves_hot_entries_and_enforces_count_weight_and_oversize_limits()
    {
        var cache = new SyntaxTokenCache<object>(capacity: 3, weightLimit: 9, entryWeightLimit: 5);
        var first = new object();
        cache.Store(("test", "first"), first, 3);
        cache.Store(("test", "second"), new object(), 3);
        cache.Store(("test", "third"), new object(), 3);
        Assert.That(cache.TryGetValue(("test", "first"), out var hit), Is.True);
        Assert.That(hit, Is.SameAs(first));
        cache.Store(("test", "fourth"), new object(), 3);
        Assert.That(cache.Count, Is.EqualTo(3));
        Assert.That(cache.Weight, Is.EqualTo(9));
        Assert.That(cache.TryGetValue(("test", "second"), out _), Is.False);
        Assert.That(cache.TryGetValue(("test", "first"), out _), Is.True);
        Assert.That(cache.Store(("test", "oversized"), new object(), 6), Is.False);
        Assert.That(cache.Count, Is.EqualTo(3), "an oversized item cannot flush the working set");
        cache.Store(("test", "weighted"), new object(), 5);
        Assert.That(cache.Weight, Is.LessThanOrEqualTo(9));
        Assert.That(cache.TryGetValue(("test", "first"), out _), Is.True);
        cache.Store(("test", "weighted"), new object(), 1);
        Assert.That(cache.Weight, Is.EqualTo(4), "replacement adjusts the retained weight exactly once");
    }

    private sealed class CountedLines(IReadOnlyList<string> source) : IReadOnlyList<string>
    {
        public int Reads;
        public int Count => source.Count;
        public string this[int index] { get { Reads++; return source[index]; } }
        public IEnumerator<string> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SingleRunTheme(string fontStyle, string background) : IRawTheme
    {
        public string GetName() => "Single run theme";
        public string GetInclude() => string.Empty;
        public ICollection<IRawThemeSetting> GetSettings() => [];
        public ICollection<IRawThemeSetting> GetTokenColors() => [new Setting(fontStyle, background)];
        public ICollection<KeyValuePair<string, object>> GetGuiColors() => [];
    }

    private sealed class Setting(string fontStyle, string background) : IRawThemeSetting
    {
        public string GetName() => "public keyword";
        public object GetScope() => "storage.modifier.public.cs";
        public IThemeSetting GetSetting() => new ThemeSetting(fontStyle, background);
    }

    private sealed class ThemeSetting(string fontStyle, string background) : IThemeSetting
    {
        public object GetFontStyle() => fontStyle;
        public string GetBackground() => background;
        public string GetForeground() => "#123456";
    }

    private static Task OnSession(Action action) => HeadlessSession.Current.Dispatch(action, CancellationToken.None);
}
