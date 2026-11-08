using Avalonia;
using Avalonia.VisualTree;
using Markdig;

namespace LiveMarkdown.Avalonia;

partial class MarkdownRenderer : IObserver<MarkdownDocumentUpdate>
{
    /// <summary>
    /// Configures the shared default pipeline used by the built-in update producer.
    /// Subscribe before <see cref="MarkdownUpdateProducer.DefaultPipeline"/> is first accessed.
    /// </summary>
    public static event Action<MarkdownPipelineBuilder>? ConfigurePipeline
    {
        add => MarkdownUpdateProducer.ConfigurePipeline += value;
        remove => MarkdownUpdateProducer.ConfigurePipeline -= value;
    }

    /// <summary>
    /// Defines the <see cref="MarkdownBuilder"/> property.
    /// </summary>
    public static readonly DirectProperty<MarkdownRenderer, ObservableStringBuilder?> MarkdownBuilderProperty =
        AvaloniaProperty.RegisterDirect<MarkdownRenderer, ObservableStringBuilder?>(
            nameof(MarkdownBuilder),
            o => o.MarkdownBuilder,
            (o, v) => o.MarkdownBuilder = v);

    /// <summary>
    /// Gets or sets the primary streaming Markdown source used by <see cref="UpdateProducer"/>.
    /// </summary>
    public ObservableStringBuilder? MarkdownBuilder
    {
        get => UpdateProducer.MarkdownBuilder;
        set
        {
            Dispatcher.VerifyAccess();
            var producer = UpdateProducer;
            var oldValue = producer.MarkdownBuilder;
            if (ReferenceEquals(oldValue, value)) return;

            producer.MarkdownBuilder = value;
            RaisePropertyChanged(MarkdownBuilderProperty, oldValue, value);
        }
    }

    /// <summary>
    /// Defines the <see cref="UpdateProducer"/> property.
    /// </summary>
    public static readonly DirectProperty<MarkdownRenderer, IMarkdownUpdateProducer> UpdateProducerProperty =
        AvaloniaProperty.RegisterDirect<MarkdownRenderer, IMarkdownUpdateProducer>(
            nameof(UpdateProducer),
            o => o.UpdateProducer,
            (o, v) => o.UpdateProducer = v);

    /// <summary>
    /// Gets or sets the source of parsed document updates.
    /// </summary>
    /// <remarks>
    /// The default producer is allocated lazily. The renderer owns only its subscription to the
    /// producer and releases that subscription when the property changes or the renderer detaches.
    /// Reattachment subscribes again and replays the latest state. Initial off-tree rendering remains supported.
    /// </remarks>
    public IMarkdownUpdateProducer UpdateProducer
    {
        get
        {
            Dispatcher.VerifyAccess();
            if (updateProducer is not null) return updateProducer;

            var producer = new MarkdownUpdateProducer();
            updateProducer = producer;
            EnsureUpdateProducerSubscription();
            return producer;
        }
        set
        {
            Dispatcher.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(updateProducer, value)) return;

            var oldProducer = updateProducer;
            var oldMarkdownBuilder = oldProducer?.MarkdownBuilder;

            updateProducerSubscription?.Dispose();
            updateProducerSubscription = null;
            SetAndRaise(UpdateProducerProperty, ref updateProducer!, value);
            EnsureUpdateProducerSubscription();

            var newMarkdownBuilder = value.MarkdownBuilder;
            if (!ReferenceEquals(oldMarkdownBuilder, newMarkdownBuilder))
            {
                RaisePropertyChanged(MarkdownBuilderProperty, oldMarkdownBuilder, newMarkdownBuilder);
            }
        }
    }

    private IMarkdownUpdateProducer? updateProducer;
    private IDisposable? updateProducerSubscription;
    private bool updateProducerSuspended;

    private void EnsureUpdateProducerSubscription()
    {
        if (updateProducerSuspended || updateProducerSubscription is not null || updateProducer is not { } producer) return;
        var subscription = producer.Subscribe(this);
        // A synchronous replay can replace the producer or detach this renderer.
        if (!updateProducerSuspended && ReferenceEquals(updateProducer, producer) && updateProducerSubscription is null)
            updateProducerSubscription = subscription;
        else subscription.Dispose();
    }

    private void SuspendUpdateProducerSubscription()
    {
        updateProducerSuspended = true;
        updateProducerSubscription?.Dispose();
        updateProducerSubscription = null;
        collectedTextBlocks = [];
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!this.IsAttachedToVisualTree()) return;
        updateProducerSuspended = false;
        EnsureUpdateProducerSubscription();
    }

    void IObserver<MarkdownDocumentUpdate>.OnCompleted()
    {
    }

    void IObserver<MarkdownDocumentUpdate>.OnError(Exception error)
    {
    }

    void IObserver<MarkdownDocumentUpdate>.OnNext(MarkdownDocumentUpdate value)
    {
        ApplyDocumentUpdate(value);
    }
}