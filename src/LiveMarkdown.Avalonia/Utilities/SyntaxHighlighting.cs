using System.Collections.Concurrent;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using FontStyle = Avalonia.Media.FontStyle;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Handles syntax highlighting for source code using TextMateSharp
/// and renders it into an Avalonia InlineCollection.
/// </summary>
public sealed class SyntaxHighlighting
{
    /// <summary>
    /// The axaml class name used to mark formatted runs.
    /// </summary>
    public const string FormattedClassName = "formatted";

    /// <summary>
    /// Checks if a Run has already been formatted.
    /// </summary>
    /// <param name="run"></param>
    /// <returns></returns>
    public static bool IsRunFormatted(Run run) => run.Classes.Contains(FormattedClassName);

    private static readonly RegistryOptions RegistryOptions;
    private static readonly Registry Registry;

    private static readonly StringComparer CustomThemeNameComparer = StringComparer.Ordinal;
    private static readonly Dictionary<string, CustomThemeRegistration> CustomThemeRegistrations = new(CustomThemeNameComparer);
    private static readonly Dictionary<ThemeName, Lazy<ThemeCacheEntry>> BuiltInThemeCache = [];
    // Strong, not weak: a grammar is tens of milliseconds to load and there are a handful of languages in
    // any document. Held weakly, a collection between two scrolls dropped it and the next code block paid
    // the load again on the UI thread, mid-scroll.
    private static readonly Dictionary<string, SyntaxHighlighting> LanguageCache = [];

    // ── Block token cache ─────────────────────────────────────────────────────────────────────────────
    // Tokenizing is the cost of a code block: TextMate walks every line, and a rich grammar (C#) takes a
    // few milliseconds per line, so a 25-line block is 50–130ms on the UI thread the first time it is
    // shown. Tokens depend only on (language, text), so they are cached by exactly that: a block shown
    // again — re-created after virtualization recycled it, or the same snippet in another view — formats
    // from cached tokens in the time it takes to make its Runs. A block that is STREAMING grows by lines;
    // its tokens are kept per collection and extended from the last line's rule stack, so each update
    // tokenizes only the new lines (previously every update restarted the stack at the first unformatted
    // line, which was also wrong inside a multi-line comment or string). Prewarm lets a host tokenize a
    // document's blocks ahead of time, off the UI thread.
    private sealed class TokenizedBlock
    {
        public readonly List<string> Lines = [];
        public readonly List<IToken[]> Tokens = [];
        public readonly List<IStateStack?> StackAfter = [];

        /// <summary>
        /// Whether this block is in the cross-block cache, and so is keyed by text it must keep
        /// matching. A block that is not may be grown in place by the collection that owns it.
        /// </summary>
        public bool Shared;
    }

    private const int BlockCacheCap = 1024;
    private static readonly Dictionary<(string Language, string Text), TokenizedBlock> BlockCache = [];
    private static readonly object BlockCacheLock = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineCollection, TokenizedBlock> LiveBlocks = new();
    private readonly object _tokenizeLock = new();

#if NET10_0_OR_GREATER
    private static readonly Lock ThemeCacheLock = new();
#else
    private static readonly object ThemeCacheLock = new();
#endif

    private readonly IGrammar? _grammar;

    static SyntaxHighlighting()
    {
        // We only need to get a Registry from it, so the ThemeName here is not used
        // for syntax highlighting. This ensures that grammars are loaded only once
        // and shared across SyntaxHighlighting instances.
        RegistryOptions = new RegistryOptions(default);
        Registry = new Registry(RegistryOptions);
    }

    /// <summary>
    /// Registers or replaces a custom TextMate theme.
    /// Custom theme names are kept separate from the built-in <see cref="ThemeName"/> values.
    /// </summary>
    /// <param name="name">The name used by <see cref="CodeBlock.CustomColorTheme"/>.</param>
    /// <param name="theme">The raw TextMate theme.</param>
    /// <exception cref="ArgumentException">Thrown when the name is empty or conflicts with a built-in theme name.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="theme"/> is null.</exception>
    public static void RegisterCustomTheme(string name, IRawTheme theme)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(theme);

        if (Enum.GetNames<ThemeName>().Any(builtInName => CustomThemeNameComparer.Equals(builtInName, name)))
        {
            throw new ArgumentException($"The custom theme name '{name}' conflicts with a built-in theme.", nameof(name));
        }

        lock (ThemeCacheLock)
        {
            var rawThemes = CustomThemeRegistrations.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.RawTheme,
                CustomThemeNameComparer);
            rawThemes[name] = theme;

            // Recreate registrations with one immutable include snapshot. This also
            // invalidates dependants when a theme included by another custom theme changes.
            CustomThemeRegistrations.Clear();
            foreach (var pair in rawThemes)
            {
                CustomThemeRegistrations.Add(
                    pair.Key,
                    new CustomThemeRegistration(pair.Value, rawThemes));
            }
        }
    }

    /// <summary>
    /// Removes a previously registered custom theme and invalidates its parsed cache.
    /// </summary>
    /// <param name="name">The registered custom theme name.</param>
    /// <returns><see langword="true"/> if a theme was removed; otherwise <see langword="false"/>.</returns>
    public static bool UnregisterCustomTheme(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (ThemeCacheLock)
        {
            if (!CustomThemeRegistrations.Remove(name)) return false;

            var rawThemes = CustomThemeRegistrations.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.RawTheme,
                CustomThemeNameComparer);

            CustomThemeRegistrations.Clear();
            foreach (var pair in rawThemes)
            {
                CustomThemeRegistrations.Add(pair.Key, new CustomThemeRegistration(pair.Value, rawThemes));
            }
            return true;
        }
    }

    /// <summary>
    /// Creates or retrieves a cached SyntaxHighlighting instance for the specified language.
    /// </summary>
    /// <param name="languageName"></param>
    /// <returns></returns>
    public static SyntaxHighlighting Create(string languageName)
    {
        lock (LanguageCache)
        {
            if (LanguageCache.TryGetValue(languageName, out var cached)) return cached;

            var instance = new SyntaxHighlighting(languageName);
            LanguageCache[languageName] = instance;
            return instance;
        }
    }

    /// <summary>Whether this language has a grammar at all; without one there is nothing to warm or format.</summary>
    public bool HasGrammar => _grammar is not null;

    /// <summary>
    /// Tokenizes <paramref name="text"/> now and keeps the result, so the first code block with this text
    /// formats from cache. Safe to call from any thread: tokenization holds the grammar's lock, and the
    /// UI thread waits at most one line's worth behind it.
    /// </summary>
    public void Prewarm(string text)
    {
        if (_grammar is null || string.IsNullOrEmpty(text)) return;
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) Array.Resize(ref lines, lines.Length - 1);
        TokenizeBlock(lines, previous: null);
    }

    /// <summary>Tokens for <paramref name="lines"/>: from the block cache if this exact text was seen, else
    /// extended from <paramref name="previous"/> (a live block that grew) where the lines still match, else
    /// tokenized from the top. The result is cached under the full text.</summary>
    private TokenizedBlock TokenizeBlock(IReadOnlyList<string> lines, TokenizedBlock? previous)
    {
        // How much of the last tokenization of this same collection still stands. Decided FIRST,
        // because it decides whether the cross-block cache below is worth consulting at all: keying
        // that cache means joining the whole block into one string and hashing it, and a streaming
        // block would pay that on every appended line to look up a key nothing has ever stored.
        var reuse = 0;
        if (previous is not null)
        {
            while (reuse < lines.Count && reuse < previous.Lines.Count && string.Equals(lines[reuse], previous.Lines[reuse], StringComparison.Ordinal)) reuse++;
        }

        // A block seen for the first time -- freshly created, or recycled by a virtualizing host --
        // reuses nothing, and is exactly the case the cross-block cache exists for.
        (string Language, string Text)? key = reuse == 0 ? (_languageName, string.Join('\n', lines)) : null;
        if (key is { } lookup)
        {
            lock (BlockCacheLock)
            {
                if (BlockCache.TryGetValue(lookup, out var hit)) return hit;
            }
        }

        // A streaming block grows by a line at a time, and rebuilding its token lists to add one
        // costs an entry per line of the whole block on every append. When the previous tokens are
        // this collection's own -- not something the cross-block cache is keyed on -- they are
        // extended in place instead, which is what makes an append cost the lines it actually added.
        TokenizedBlock block;
        if (previous is { Shared: false })
        {
            block = previous;
            if (block.Lines.Count > reuse)
            {
                block.Lines.RemoveRange(reuse, block.Lines.Count - reuse);
                block.Tokens.RemoveRange(reuse, block.Tokens.Count - reuse);
                block.StackAfter.RemoveRange(reuse, block.StackAfter.Count - reuse);
            }
        }
        else
        {
            block = new TokenizedBlock();
            for (var i = 0; i < reuse; i++)
            {
                block.Lines.Add(previous!.Lines[i]);
                block.Tokens.Add(previous.Tokens[i]);
                block.StackAfter.Add(previous.StackAfter[i]);
            }
        }

        var stack = reuse > 0 ? block.StackAfter[reuse - 1] : null;
        lock (_tokenizeLock)
        {
            for (var i = reuse; i < lines.Count; i++)
            {
                var result = _grammar!.TokenizeLine(lines[i], stack, TimeSpan.MaxValue);
                stack = result.RuleStack;
                block.Lines.Add(lines[i]);
                block.Tokens.Add(result.Tokens);
                block.StackAfter.Add(stack);
            }
        }

        if (key is { } store)
        {
            lock (BlockCacheLock)
            {
                if (BlockCache.Count >= BlockCacheCap) BlockCache.Clear();   // a simple bound; a whole document fits many times over
                BlockCache[store] = block;
                block.Shared = true;   // keyed by its text now, so it may no longer be grown in place
            }
        }

        return block;
    }

    private static ThemeCacheEntry GetBuiltInThemeCacheEntry(ThemeName themeName)
    {
        Lazy<ThemeCacheEntry>? cache;
        lock (ThemeCacheLock)
        {
            if (!BuiltInThemeCache.TryGetValue(themeName, out cache))
            {
                cache = new Lazy<ThemeCacheEntry>(
                    () => new ThemeCacheEntry(RegistryOptions.LoadTheme(themeName), new ThemeRegistryOptions(RegistryOptions, EmptyCustomThemes)),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                BuiltInThemeCache.Add(themeName, cache);
            }
        }

        return cache.Value;
    }

    private static ThemeCacheEntry ResolveTheme(ThemeName fallbackThemeName, string? customThemeName)
    {
        if (!string.IsNullOrWhiteSpace(customThemeName))
        {
            CustomThemeRegistration? registration;

            lock (ThemeCacheLock)
            {
                CustomThemeRegistrations.TryGetValue(customThemeName, out registration);
            }

            if (registration is not null) return registration.Cache.Value;
        }

        return GetBuiltInThemeCacheEntry(fallbackThemeName);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SyntaxHighlighting"/> class.
    /// </summary>
    /// <param name="languageName"></param>
    private readonly string _languageName;

    private SyntaxHighlighting(string languageName)
    {
        _languageName = languageName;
        var scopeName = RegistryOptions.GetScopeByLanguageId(languageName) ?? RegistryOptions.GetScopeByExtension('.' + languageName);
        if (scopeName == null) return;

        _grammar = Registry.LoadGrammar(scopeName);
    }

    /// <summary>
    /// Formats the source code and populates the InlineCollection with styled runs.
    /// </summary>
    /// <param name="inlines">The inlines containing the source code.</param>
    /// <param name="themeName">The built-in fallback theme.</param>
    /// <param name="customThemeName">An optional registered custom theme name.</param>
    public void FormatInlines(InlineCollection inlines, ThemeName themeName = ThemeName.DarkPlus, string? customThemeName = null) =>
        FormatInlines([inlines], themeName, customThemeName);

    /// <summary>
    /// Formats one block of code that is laid out across several inline collections.
    /// </summary>
    /// <remarks>
    /// The collections are treated as ONE run of lines, because tokenization is stateful: a comment
    /// or a string opened on the last line of one has to still be open on the first line of the next,
    /// and formatting them independently would end every one of them at a boundary that is a layout
    /// detail rather than anything in the code.
    /// </remarks>
    /// <param name="chunks">The inline collections holding the block's lines, in order.</param>
    /// <param name="themeName">The built-in fallback theme.</param>
    /// <param name="customThemeName">An optional registered custom theme name.</param>
    internal void FormatInlines(
        IReadOnlyList<InlineCollection> chunks,
        ThemeName themeName = ThemeName.DarkPlus,
        string? customThemeName = null,
        IReadOnlyList<string>? knownLineTexts = null)
    {
        if (_grammar is null || chunks.Count == 0) return;

        var theme = ResolveTheme(themeName, customThemeName);

        // Where each chunk's lines begin. A chunk alternates line, break, line, so its inline count
        // gives its line count, and a line index then resolves to its inline by arithmetic. Walking
        // the inlines to build that map instead cost a list entry per line of the whole block on
        // every append, when what needs formatting is almost always just the tail.
        var chunkFirstLine = new int[chunks.Count + 1];
        for (var c = 0; c < chunks.Count; c++)
        {
            chunkFirstLine[c + 1] = chunkFirstLine[c] + ((chunks[c].Count + 1) / 2);
        }

        var lineCount = chunkFirstLine[chunks.Count];
        if (lineCount == 0) return;

        // Tokenizing needs every line's text, in order, to carry the rule stack through the block.
        // The caller usually has it already; only a caller that does not pays to rebuild it.
        IReadOnlyList<string> lines;
        if (knownLineTexts is not null && knownLineTexts.Count == lineCount)
        {
            lines = knownLineTexts;
        }
        else
        {
            var built = new List<string>(lineCount);
            for (var li = 0; li < lineCount; li++)
            {
                built.Add(InlineAt(chunks, chunkFirstLine, li) switch
                {
                    Run r => r.Text ?? "",
                    Span sp => TextOf(sp),
                    _ => "",
                });
            }

            lines = built;
        }

        // Kept against the FIRST collection, which identifies the block for as long as it lives.
        LiveBlocks.TryGetValue(chunks[0], out var previous);
        var block = TokenizeBlock(lines, previous);
        LiveBlocks.AddOrUpdate(chunks[0], block);

        var chunkIndex = 0;
        for (var li = 0; li < lineCount; li++)
        {
            while (li >= chunkFirstLine[chunkIndex + 1]) chunkIndex++;

            var inlines = chunks[chunkIndex];
            var i = (li - chunkFirstLine[chunkIndex]) * 2;
            if (inlines[i] is not Run { Text: { } line } run) continue;
            if (IsRunFormatted(run)) continue;
            var tokens = block.Tokens[li];

            if (tokens.Length == 1)
            {
                StyleRun(run, tokens[0].Scopes, theme);
                continue;
            }

            // One Run per run of tokens that LOOK the same, rather than one per token. A boundary
            // the theme gives the same colour, weight and decorations to either side of is invisible
            // on screen, and costs a separately shaped run in every measure of the block for as long
            // as the block exists -- a C# line is commonly ten tokens wearing three or four styles,
            // and punctuation between two identically coloured tokens splits a run for nothing.
            var styles = new TokenStyle[tokens.Length];
            for (var t = 0; t < tokens.Length; t++)
            {
                styles[t] = ResolveStyle(tokens[t].Scopes, theme);
            }

            var groups = 1;
            for (var t = 1; t < tokens.Length; t++)
            {
                if (!CanMerge(styles[t], styles[t - 1])) groups++;
            }

            if (groups == 1)
            {
                // The whole line looks the same. Style the Run already there and build no Span at
                // all -- a comment, a string, or an unrecognized line is this, and it is common.
                ApplyStyle(run, styles[0], theme);
                continue;
            }

            Span span;
            inlines[i] = span = new Span();
            var groupStart = 0;
            for (var t = 1; t <= tokens.Length; t++)
            {
                if (t < tokens.Length && CanMerge(styles[t], styles[t - 1])) continue;

                var start = tokens[groupStart].StartIndex;
                var end = tokens[t - 1].EndIndex;
                var merged = new Run(line.Substring(start, Math.Min(end - start, line.Length - start)));
                ApplyStyle(merged, styles[groupStart], theme);
                span.Inlines.Add(merged);
                groupStart = t;
            }
        }
    }

    /// <summary>
    /// Applies styling to a Run based on the token's scopes and the current theme.
    /// </summary>
    /// <param name="run">The Run to style.</param>
    /// <param name="scopes">The scopes associated with the token.</param>
    /// <param name="theme">The resolved theme to use for styling.</param>
    /// <summary>The inline holding a line, found by arithmetic rather than by walking.</summary>
    private static Inline? InlineAt(IReadOnlyList<InlineCollection> chunks, int[] chunkFirstLine, int line)
    {
        var chunk = 0;
        while (line >= chunkFirstLine[chunk + 1]) chunk++;

        var index = (line - chunkFirstLine[chunk]) * 2;
        var inlines = chunks[chunk];
        return index < inlines.Count ? inlines[index] : null;
    }

    /// <summary>The length of a span's text, without building it.</summary>
    private static int TextLengthOf(Span span)
    {
        var length = 0;
        foreach (var inline in span.Inlines)
            if (inline is Run r) length += r.Text?.Length ?? 0;
        return length;
    }

    private static string TextOf(Span span)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in span.Inlines)
            if (inline is Run r) sb.Append(r.Text);
        return sb.ToString();
    }

    private static void StyleRun(Run run, IList<string> scopes, ThemeCacheEntry theme) =>
        ApplyStyle(run, ResolveStyle(scopes, theme), theme);

    /// <summary>
    /// Everything about a token the theme decides, and so everything that makes two adjacent tokens
    /// need separate Runs. Comparing these is what lets a line merge.
    /// </summary>
    private readonly record struct TokenStyle(int Foreground, int Background, TextMateSharp.Themes.FontStyle FontStyle);

    /// <summary>
    /// Turns run merging off, so both representations can be rendered and compared. Nothing but the
    /// raster-equality test should touch this.
    /// </summary>
    internal static bool MergeAdjacentRuns = true;

    private static bool CanMerge(TokenStyle left, TokenStyle right) => MergeAdjacentRuns && left == right;

    /// <summary>Resolves a token's scopes to the style the theme gives it.</summary>
    private static TokenStyle ResolveStyle(IList<string> scopes, ThemeCacheEntry theme)
    {
        var themeRules = theme.Theme.Match(scopes);

        var foregroundId = -1;
        var backgroundId = -1;
        var fontStyle = TextMateSharp.Themes.FontStyle.NotSet;

        // Determine the style from the matched theme rules.
        foreach (var themeRule in themeRules)
        {
            if (foregroundId == -1 && themeRule.foreground > 0)
                foregroundId = themeRule.foreground;

            if (backgroundId == -1 && themeRule.background > 0)
                backgroundId = themeRule.background;

            if (fontStyle == TextMateSharp.Themes.FontStyle.NotSet && themeRule.fontStyle > 0)
                fontStyle = themeRule.fontStyle;
        }

        return new TokenStyle(foregroundId, backgroundId, fontStyle);
    }

    /// <summary>Applies a resolved style to a Run.</summary>
    private static void ApplyStyle(Run run, TokenStyle style, ThemeCacheEntry theme)
    {
        if (!IsRunFormatted(run)) run.Classes.Add(FormattedClassName);

        if (theme.GetBrush(style.Foreground) is { } foreground)
            run.Foreground = foreground;

        if (theme.GetBrush(style.Background) is { } background)
            run.Background = background;

        // Apply font styles.
        var fontStyle = style.FontStyle;
        if (fontStyle == TextMateSharp.Themes.FontStyle.NotSet) return;

        if ((fontStyle & TextMateSharp.Themes.FontStyle.Italic) != 0) run.FontStyle = FontStyle.Italic;
        if ((fontStyle & TextMateSharp.Themes.FontStyle.Bold) != 0) run.FontWeight = FontWeight.Bold;
        if ((fontStyle & TextMateSharp.Themes.FontStyle.Underline) != 0) ApplyDecoration(TextDecorations.Underline);
        if ((fontStyle & TextMateSharp.Themes.FontStyle.Strikethrough) != 0) ApplyDecoration(TextDecorations.Strikethrough);

        void ApplyDecoration(TextDecorationCollection decorations)
        {
            if (run.TextDecorations is null)
            {
                run.TextDecorations = decorations;
            }
            else
            {
                run.TextDecorations.AddRange(decorations);
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, IRawTheme> EmptyCustomThemes =
        new Dictionary<string, IRawTheme>(CustomThemeNameComparer);

    /// <summary>
    /// A cached, parsed TextMate theme and its immutable brush cache.
    /// </summary>
    private sealed class ThemeCacheEntry(IRawTheme rawTheme, IRegistryOptions registryOptions)
    {
        public Theme Theme { get; } = Theme.CreateFromRawTheme(rawTheme, registryOptions);

        private readonly ConcurrentDictionary<int, IBrush> _colorBrushCache = new();

        public IBrush? GetBrush(int colorId)
        {
            if (colorId <= 0) return null;

            if (_colorBrushCache.TryGetValue(colorId, out var cachedBrush))
                return cachedBrush;

            var colorString = Theme.GetColor(colorId);
            if (!Color.TryParse(colorString, out var color)) return null;

            return _colorBrushCache.GetOrAdd(colorId, static (_, parsedColor) => new ImmutableSolidColorBrush(parsedColor), color);
        }
    }

    private sealed class CustomThemeRegistration(IRawTheme rawTheme, IReadOnlyDictionary<string, IRawTheme> customThemes)
    {
        public IRawTheme RawTheme { get; } = rawTheme;

        public Lazy<ThemeCacheEntry> Cache { get; } = new(
            () => new ThemeCacheEntry(rawTheme, new ThemeRegistryOptions(RegistryOptions, customThemes)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private sealed class ThemeRegistryOptions(IRegistryOptions fallback, IReadOnlyDictionary<string, IRawTheme> customThemes) : IRegistryOptions
    {
        public IRawTheme GetTheme(string scopeName)
        {
            return customThemes.TryGetValue(scopeName, out var theme) ? theme : fallback.GetTheme(scopeName);
        }

        public IRawGrammar GetGrammar(string scopeName) => fallback.GetGrammar(scopeName);

        public ICollection<string> GetInjections(string scopeName) => fallback.GetInjections(scopeName);

        public IRawTheme GetDefaultTheme() => fallback.GetDefaultTheme();
    }
}
