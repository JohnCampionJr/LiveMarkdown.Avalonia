using System.Collections.Immutable;
using Avalonia;
using Avalonia.Threading;
using Markdig;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Parses an <see cref="ObservableStringBuilder"/> and publishes immutable document updates.
/// </summary>
/// <remarks>
/// The producer retains its most recent successful update for replay. It observes and parses its
/// source only while it has subscribers. All access must occur on the Avalonia UI thread.
/// </remarks>
public sealed class MarkdownUpdateProducer : AvaloniaObject, IMarkdownUpdateProducer
{
    private static readonly Lazy<MarkdownPipeline> DefaultPipelineSource = new(() =>
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseCodeBlockSpanFixer();
        defaultSupportsFenceAppend = ConfigurePipeline is null;
        ConfigurePipeline?.Invoke(builder);
        return builder.Build();
    });

    private static bool defaultSupportsFenceAppend;

    /// <summary>
    /// Gets the shared default Markdig pipeline. The pipeline is created when first accessed.
    /// </summary>
    public static MarkdownPipeline DefaultPipeline => DefaultPipelineSource.Value;

    /// <summary>
    /// Optional callback to configure the shared default pipeline before it is built.
    /// Subscribe before <see cref="DefaultPipeline"/> is first accessed.
    /// </summary>
    public static event Action<MarkdownPipelineBuilder>? ConfigurePipeline;

    /// <inheritdoc/>
    public ObservableStringBuilder? MarkdownBuilder
    {
        get
        {
            Dispatcher.VerifyAccess();
            return markdownBuilder;
        }
        set
        {
            Dispatcher.VerifyAccess();
            if (ReferenceEquals(markdownBuilder, value)) return;

            if (!observers.IsEmpty && markdownBuilder is not null)
            {
                markdownBuilder.Changed -= CommitChange;
            }

            markdownBuilder = value;
            if (!observers.IsEmpty && value is not null)
            {
                value.Changed += CommitChange;
            }

            RestartParsing();
        }
    }

    /// <summary>
    /// Gets or sets the Markdig pipeline used to parse snapshots.
    /// </summary>
    /// <remarks>
    /// Changing the pipeline invalidates the retained update and publishes the next successful
    /// parse as a complete update.
    /// </remarks>
    public MarkdownPipeline Pipeline
    {
        get
        {
            Dispatcher.VerifyAccess();
            return field ??= DefaultPipeline;
        }
        set
        {
            Dispatcher.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(field, value)) return;

            field = value;
            RestartParsing();
        }
    }

    /// <summary>
    /// Gets or sets whether an append may be parsed as a trailing region of the document already
    /// parsed, rather than by parsing the whole source again. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>The result is the same document either way — <see cref="IncrementalParse"/> says what
    /// makes that true and which documents it refuses — but the cost is not. A keystroke into a
    /// quarter-megabyte transcript re-parses about 250 KB of prose, which is 12 ms and 4 MB, and
    /// copies the source to hand it to the parser, which is another 500 KB. Parsing the tail pays for
    /// the block that changed.</para>
    ///
    /// <para>Turning it off makes every parse a whole-document parse, which is what the equality
    /// tests compare against. Changing it invalidates the retained update.</para>
    /// </remarks>
    public bool IncrementalParsing
    {
        get
        {
            Dispatcher.VerifyAccess();
            return field;
        }
        set
        {
            Dispatcher.VerifyAccess();
            if (field == value) return;

            field = value;
            RestartParsing();
        }
    } = true;

    private ImmutableArray<Subscription> observers = [];
    private ObservableStringBuilder? markdownBuilder;
    private MarkdownDocumentUpdate? currentUpdate;
    private ObservableStringBuilderChangedEventArgs? pendingChange;
    private Task? parseTask;
    private int parseGeneration;
    private bool forceFullUpdate;

    /// <summary>The document last published, mutated in place by each splice.</summary>
    private Markdig.Syntax.MarkdownDocument? liveDocument;

    /// <summary>The offset a splice re-parses from, or -1 when there is no usable restart point.</summary>
    private int restartOffset = -1;
    private OpenFenceAppend? openFence;
    private int parsedSourceLength;
    private bool sourceEndedWithCarriageReturn;
    internal long ParsedCharacters { get; private set; }
    internal int FenceAppendCount { get; private set; }

    /// <summary>Whether the document is free of constructs a later line can reach back through.</summary>
    private bool documentIsSpliceable;

    /// <summary>Set when one update has to be parsed whole before splicing can resume.</summary>
    private bool forceFullParseOnce;

    /// <summary>Counts the appends parsed as a tail rather than whole. Read by the tests.</summary>
    internal int SpliceCount { get; private set; }

    /// <inheritdoc/>
    public IDisposable Subscribe(IObserver<MarkdownDocumentUpdate> observer)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(observer);

        var activate = observers.IsEmpty;
        var subscription = new Subscription(this, observer);
        observers = observers.Add(subscription);
        if (activate)
        {
            Activate();
        }

        if (currentUpdate is { } update)
        {
            subscription.Publish(update);
        }

        return subscription;
    }

    private void Activate()
    {
        if (markdownBuilder is not { } source) return;

        source.Changed += CommitChange;
        if (currentUpdate?.Version == source.Version) return;

        RestartParsing();
    }

    private void RestartParsing()
    {
        parseGeneration++;
        pendingChange = null;
        currentUpdate = null;
        forceFullUpdate = true;
        liveDocument = null;
        restartOffset = -1;
        openFence = null;
        parsedSourceLength = 0;
        sourceEndedWithCarriageReturn = false;
        documentIsSpliceable = false;
        forceFullParseOnce = false;

        if (observers.IsEmpty || markdownBuilder is not { } source) return;

        pendingChange = new ObservableStringBuilderChangedEventArgs(0, source.Length, source.Length, source.Version);
        EnsureParseLoopStarted();
    }

    private void CommitChange(in ObservableStringBuilderChangedEventArgs change)
    {
        Dispatcher.VerifyAccess();

        if (pendingChange is not { } pending)
        {
            pendingChange = change;
        }
        else
        {
            var startIndex = Math.Min(pending.StartIndex, change.StartIndex);
            var endIndex = Math.Max(pending.StartIndex + pending.Length, change.StartIndex + change.Length);
            pendingChange = new ObservableStringBuilderChangedEventArgs(
                startIndex,
                endIndex - startIndex,
                change.NewLength,
                change.Version);
        }

        EnsureParseLoopStarted();
    }

    private void EnsureParseLoopStarted()
    {
        Dispatcher.VerifyAccess();
        if (observers.IsEmpty || markdownBuilder is null || pendingChange is null || parseTask is { IsCompleted: false }) return;

        parseTask = ParsePendingChangesAsync();
    }

    private async Task ParsePendingChangesAsync()
    {
        // Always yield once so parseTask is assigned before the loop can complete.
        await Task.Yield();

        try
        {
            while (!observers.IsEmpty && markdownBuilder is { } source && pendingChange is { } change)
            {
                var generation = parseGeneration;
                var currentPipeline = Pipeline;

                // Only the last top-level block can be extended by text appended at the end, so a
                // change that starts at or after that block re-parses correctly on its own.
                var splice = IncrementalParsing
                    && !forceFullParseOnce
                    && !forceFullUpdate
                    && currentUpdate is not null
                    && liveDocument is not null
                    && documentIsSpliceable
                    && restartOffset > 0
                    && change.StartIndex >= restartOffset;
                var appendFence = IncrementalParsing && !forceFullParseOnce && !forceFullUpdate &&
                    currentUpdate is not null && liveDocument is not null && documentIsSpliceable &&
                    openFence is not null && !sourceEndedWithCarriageReturn &&
                    change.StartIndex == parsedSourceLength && change.NewLength > parsedSourceLength &&
                    change.Length == change.NewLength - parsedSourceLength;
                var textOffset = appendFence ? openFence!.SnapshotOffset(parsedSourceLength) : splice ? restartOffset : 0;

                var snapshot = splice || appendFence ? source.CaptureSnapshot(textOffset) : source.CaptureSnapshot();
                if (snapshot.Version != change.Version)
                {
                    continue;
                }

                var time = DateTimeOffset.UtcNow;
                Markdig.Syntax.MarkdownDocument document;
                try
                {
                    var parseText = appendFence ? openFence!.OpeningLine + snapshot.Text : snapshot.Text;
                    ParsedCharacters += parseText.Length;
                    document = await Task.Run(() => Markdown.Parse(parseText, currentPipeline));
                }
                catch (Exception ex)
                {
                    Dispatcher.VerifyAccess();
                    if (generation != parseGeneration) continue;

                    if (pendingChange is { } failedChange && failedChange.Version == change.Version)
                    {
                        pendingChange = null;
                        forceFullUpdate = true;
                    }

                    if (MarkdownRenderer.VerboseLogger?.IsValid is true)
                    {
                        MarkdownRenderer.VerboseLogger.Value.Log(this, "Error parsing markdown: {Message}", ex.Message);
                    }

                    continue;
                }

                Dispatcher.VerifyAccess();
                if (generation != parseGeneration) continue;
                if (pendingChange is not { } latestChange || latestChange.Version != change.Version) continue;

                int? unchangedPrefixBlockCount = null;
                if (appendFence)
                {
                    if (!openFence!.Apply(liveDocument!, document, textOffset, out var retainedBlocks))
                    {
                        // A closing fence or a changed construct must be parsed in its real context.
                        forceFullParseOnce = true;
                        forceFullUpdate = true;
                        continue;
                    }
                    document = liveDocument!;
                    unchangedPrefixBlockCount = retainedBlocks;
                    SpliceCount++;
                    FenceAppendCount++;
                }
                else if (splice)
                {
                    if (!IncrementalParse.CanSplice(document))
                    {
                        // The appended text defines a link, a footnote or an abbreviation, any of
                        // which can rewrite text above the restart point. Take the document whole,
                        // this time and from here on.
                        documentIsSpliceable = false;
                        forceFullUpdate = true;
                        continue;
                    }

                    IncrementalParse.Shift(document, textOffset);
                    if (!IncrementalParse.TrySplice(liveDocument!, textOffset, document, out var retainedBlocks))
                    {
                        // The tail names a heading the document did not already carry, so a link to
                        // it anywhere above would resolve differently. Take the whole source once;
                        // the next append splices again.
                        forceFullParseOnce = true;
                        forceFullUpdate = true;
                        continue;
                    }

                    document = liveDocument!;
                    unchangedPrefixBlockCount = retainedBlocks;
                    SpliceCount++;
                }
                else
                {
                    documentIsSpliceable = IncrementalParse.CanSplice(document);
                    // Definition-bearing parses can change the meaning of unchanged source
                    // above the edit. Their nodes need a full render, not merely a full parse.
                    if (!documentIsSpliceable) forceFullUpdate = true;
                    forceFullParseOnce = false;
                }

                liveDocument = document;
                if (!appendFence)
                {
                    restartOffset = IncrementalParse.RestartOffset(document, snapshot.Text, textOffset, restartOffset);
                    openFence = ReferenceEquals(currentPipeline, DefaultPipeline) && defaultSupportsFenceAppend && !currentPipeline.TrackTrivia
                        ? OpenFenceAppend.Create(document, snapshot.Text, textOffset) : null;
                }
                parsedSourceLength = change.NewLength;
                sourceEndedWithCarriageReturn = snapshot.Text.EndsWith('\r');

                if (MarkdownRenderer.VerboseLogger?.IsValid is true)
                {
                    MarkdownRenderer.VerboseLogger.Value.Log(
                        this,
                        "Parse markdown in {TotalMilliseconds} ms.",
                        (DateTimeOffset.UtcNow - time).TotalMilliseconds);
                }

                MarkdownDocumentUpdate update;
                if (currentUpdate is not { } previous || forceFullUpdate)
                {
                    update = new MarkdownDocumentUpdate.Full(document, change.Version);
                    forceFullUpdate = false;
                }
                else
                {
                    update = new MarkdownDocumentUpdate.Incremental(previous, document, change, unchangedPrefixBlockCount);
                }

                pendingChange = null;
                currentUpdate = update;
                Publish(update);
            }
        }
        finally
        {
            parseTask = null;
            if (!observers.IsEmpty && markdownBuilder is not null && pendingChange is not null)
            {
                Dispatcher.Post(EnsureParseLoopStarted, DispatcherPriority.Normal);
            }
        }
    }

    private void Publish(MarkdownDocumentUpdate update)
    {
        var currentObservers = observers;
        foreach (var observer in currentObservers)
        {
            observer.Publish(update);
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        Dispatcher.VerifyAccess();
        observers = observers.Remove(subscription);
        if (!observers.IsEmpty) return;

        if (markdownBuilder is not null)
        {
            markdownBuilder.Changed -= CommitChange;
        }

        parseGeneration++;
        pendingChange = null;
    }

    private sealed class Subscription(MarkdownUpdateProducer producer, IObserver<MarkdownDocumentUpdate> observer) : IDisposable
    {
        private MarkdownUpdateProducer? producer = producer;

        public void Dispose()
        {
            var currentProducer = producer;
            if (currentProducer is null) return;

            producer = null;
            currentProducer.Unsubscribe(this);
        }

        public void Publish(MarkdownDocumentUpdate update)
        {
            if (producer is not null)
            {
                observer.OnNext(update);
            }
        }
    }
}
