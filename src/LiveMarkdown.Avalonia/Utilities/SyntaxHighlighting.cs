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
        var key = (_languageName, string.Join('\n', lines));
        lock (BlockCacheLock)
        {
            if (BlockCache.TryGetValue(key, out var hit)) return hit;
        }

        var block = new TokenizedBlock();
        var reuse = 0;
        if (previous is not null)
        {
            while (reuse < lines.Count && reuse < previous.Lines.Count && string.Equals(lines[reuse], previous.Lines[reuse], StringComparison.Ordinal)) reuse++;
            for (var i = 0; i < reuse; i++)
            {
                block.Lines.Add(previous.Lines[i]);
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

        lock (BlockCacheLock)
        {
            if (BlockCache.Count >= BlockCacheCap) BlockCache.Clear();   // a simple bound; a whole document fits many times over
            BlockCache[key] = block;
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
    public void FormatInlines(InlineCollection inlines, ThemeName themeName = ThemeName.DarkPlus, string? customThemeName = null)
    {
        if (_grammar is null) return;

        var theme = ResolveTheme(themeName, customThemeName);

        // The block's lines, formatted or not: a formatted line is a styled Run or a Span of styled Runs
        // (see below); its text is needed to key the cache and to carry the rule stack into the next line.
        var lines = new List<string>(inlines.Count / 2 + 1);
        var lineOf = new List<int>(inlines.Count / 2 + 1);   // inline index of each line
        for (var i = 0; i < inlines.Count; i++)
        {
            switch (inlines[i])
            {
                case Run r: lines.Add(r.Text ?? ""); lineOf.Add(i); break;
                case Span sp: lines.Add(TextOf(sp)); lineOf.Add(i); break;
            }
        }
        LiveBlocks.TryGetValue(inlines, out var previous);
        var block = TokenizeBlock(lines, previous);
        LiveBlocks.AddOrUpdate(inlines, block);

        for (var li = 0; li < lines.Count; li++)
        {
            var i = lineOf[li];
            if (inlines[i] is not Run { Text: { } line } run) continue;
            if (IsRunFormatted(run)) continue;
            var tokens = block.Tokens[li];

            if (tokens.Length == 1)
            {
                StyleRun(run, tokens[0].Scopes, theme);
            }
            else
            {
                // Create and style a Run for each token.
                Span span;
                inlines[i] = span = new Span();
                foreach (var token in tokens)
                {
                    var text = line.Substring(token.StartIndex, Math.Min(token.EndIndex - token.StartIndex, line.Length - token.StartIndex));
                    run = new Run(text);
                    StyleRun(run, token.Scopes, theme);
                    span.Inlines.Add(run);
                }
            }
        }
    }

    /// <summary>
    /// Applies styling to a Run based on the token's scopes and the current theme.
    /// </summary>
    /// <param name="run">The Run to style.</param>
    /// <param name="scopes">The scopes associated with the token.</param>
    /// <param name="theme">The resolved theme to use for styling.</param>
    private static string TextOf(Span span)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in span.Inlines)
            if (inline is Run r) sb.Append(r.Text);
        return sb.ToString();
    }

    private static void StyleRun(Run run, IList<string> scopes, ThemeCacheEntry theme)
    {
        if (!IsRunFormatted(run)) run.Classes.Add(FormattedClassName);

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

        if (theme.GetBrush(foregroundId) is { } foreground)
            run.Foreground = foreground;

        if (theme.GetBrush(backgroundId) is { } background)
            run.Background = background;

        // Apply font styles.
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
