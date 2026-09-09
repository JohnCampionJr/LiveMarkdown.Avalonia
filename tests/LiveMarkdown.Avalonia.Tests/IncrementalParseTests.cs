using System.Diagnostics;
using System.Text;
using Avalonia.Headless;
using Avalonia.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>
/// Parsing only the trailing region of a document has one job: to produce the document a whole
/// parse would have produced. These tests hold it to that, construct by construct.
/// </summary>
/// <remarks>
/// The interesting cases are the ones where an appended line changes the meaning of the line above
/// it — a lazy continuation, a setext underline, a table header, a definition term — and the ones
/// where it can change something much further up, which is what the producer refuses to splice.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class IncrementalParseTests
{
    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession() => session = HeadlessSession.Current;

    private static readonly string[] Seeds =
    [
        "Alpha para with **bold** and `code`.\n\nBeta para.\n\n",
        "Alpha.\n\n- one\n- two\n",
        "Alpha.\n\n```csharp\nvar x = 1;\n",
        "# Title\n\nSome text\n",
        "Alpha.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n",
        "Alpha.\n\n> quote line\n",
        "Alpha.\n\n    indented code\n",
        "Term\n",
        "Alpha.\n\n1. first\n2. second\n",
        "Alpha.\n\n<div>\n",
    ];

    private static readonly string[] Appends =
    [
        "New para here.\n\n",
        "- three\n",
        "more code;\n",
        "\n> q\n",
        "| 3 | 4 |\n",
        "lazy continuation\n",
        "    more code\n",
        ": definition\n",
        "=====\n",
        "\n```\nclosed\n```\n",
        "\n",
        "   \n\n",
    ];

    [Test]
    public async Task A_spliced_document_is_the_one_a_whole_parse_gives()
    {
        var result = await session.Dispatch(
            () =>
            {
                var mismatches = new List<string>();
                var splices = 0;

                foreach (var seed in Seeds)
                {
                    foreach (var append in Appends)
                    {
                        using var host = new ProducerHost();
                        host.Append(seed);
                        var before = host.Producer.SpliceCount;
                        host.Append(append);
                        splices += host.Producer.SpliceCount - before;

                        var spliced = Describe(host.Document);
                        var whole = Describe(Markdown.Parse(seed + append, MarkdownUpdateProducer.DefaultPipeline));
                        if (spliced != whole)
                        {
                            mismatches.Add(
                                $"seed {Escape(seed)} + append {Escape(append)}\n--- spliced ---\n{spliced}--- whole ---\n{whole}");
                        }
                    }
                }

                return (Mismatches: mismatches, Splices: splices);
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Mismatches, Is.Empty, string.Join("\n\n", result.Mismatches));
            Assert.That(result.Splices, Is.GreaterThan(Seeds.Length), "sanity: the appends were actually spliced");
        });
    }

    [Test]
    public async Task A_run_of_appends_stays_the_document_a_whole_parse_gives()
    {
        // One append is the easy case. This is the one that would catch a restart point that drifts:
        // each splice computes the next one from the text it was given rather than from the source.
        var result = await session.Dispatch(
            () =>
            {
                using var host = new ProducerHost();
                var source = new StringBuilder();

                for (var i = 0; i < 40; i++)
                {
                    var text = (i % 4) switch
                    {
                        0 => $"Paragraph {i} with **bold** text.\n\n",
                        1 => $"- list item {i}\n",
                        2 => $"\n```csharp\nvar value{i} = {i};\n```\n\n",
                        _ => $"## Heading {i}\n\n",
                    };

                    source.Append(text);
                    host.Append(text);
                }

                var whole = Markdown.Parse(source.ToString(), MarkdownUpdateProducer.DefaultPipeline);
                return (Spliced: Describe(host.Document), Whole: Describe(whole), Splices: host.Producer.SpliceCount);
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Spliced, Is.EqualTo(result.Whole));
            // Ten of the forty appends are a new heading, and a new heading is a new definition
            // that a reference anywhere above could resolve against, so each costs a whole parse.
            Assert.That(result.Splices, Is.EqualTo(28), "sanity: everything but the headings was spliced");
        });
    }

    [Test]
    public async Task A_link_reference_definition_takes_the_whole_document_again()
    {
        // A definition appended at the end turns a bare [ref] at the TOP of the document into a
        // link, which is exactly what a spliced tail cannot see. The producer has to notice and stop.
        var result = await session.Dispatch(
            () =>
            {
                using var host = new ProducerHost();
                host.Append("See [ref] up here.\n\nA middle paragraph.\n\n");
                host.Append("Another paragraph.\n\n");
                var beforeDefinition = host.Producer.SpliceCount;

                host.Append("[ref]: https://example.com/\n\n");
                var afterDefinition = host.Producer.SpliceCount;

                host.Append("And one more paragraph.\n\n");
                var afterMore = host.Producer.SpliceCount;

                var source = "See [ref] up here.\n\nA middle paragraph.\n\nAnother paragraph.\n\n"
                    + "[ref]: https://example.com/\n\nAnd one more paragraph.\n\n";

                return (
                    Spliced: Describe(host.Document),
                    Whole: Describe(Markdown.Parse(source, MarkdownUpdateProducer.DefaultPipeline)),
                    BeforeDefinition: beforeDefinition,
                    AfterDefinition: afterDefinition,
                    AfterMore: afterMore,
                    Links: host.Document.Descendants().OfType<LinkInline>().Count());
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.BeforeDefinition, Is.GreaterThan(0), "sanity: it was splicing before the definition");
            Assert.That(result.AfterDefinition, Is.EqualTo(result.BeforeDefinition), "the definition was not spliced");
            Assert.That(result.AfterMore, Is.EqualTo(result.BeforeDefinition), "and it does not splice again after one");
            Assert.That(result.Links, Is.EqualTo(1), "the [ref] at the top became a link");
            Assert.That(result.Spliced, Is.EqualTo(result.Whole));
        });
    }

    [Test]
    public async Task Turning_incremental_parsing_off_still_produces_the_same_document()
    {
        var result = await session.Dispatch(
            () =>
            {
                var source = new StringBuilder();
                using var on = new ProducerHost();
                using var off = new ProducerHost(incremental: false);

                for (var i = 0; i < 12; i++)
                {
                    var text = $"Paragraph {i} with *emphasis* and a [link](https://example.com/{i}).\n\n";
                    source.Append(text);
                    on.Append(text);
                    off.Append(text);
                }

                return (On: Describe(on.Document), Off: Describe(off.Document), OffSplices: off.Producer.SpliceCount);
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.OffSplices, Is.Zero, "sanity: the switch turned it off");
            Assert.That(result.On, Is.EqualTo(result.Off));
        });
    }

    /// <summary>Every block and inline in the document, by type, span and text.</summary>
    private static string Describe(MarkdownDocument document)
    {
        var text = new StringBuilder();
        foreach (var descendant in document.Descendants())
        {
            text.Append(descendant.GetType().Name).Append(' ').Append(descendant.Span.ToString());
            switch (descendant)
            {
                case LiteralInline literal:
                    text.Append(" \"").Append(literal.Content.ToString()).Append('"');
                    break;
                case Markdig.Syntax.Inlines.CodeInline code:
                    text.Append(" `").Append(code.Content).Append('`');
                    break;
                case LinkInline link:
                    text.Append(" -> ").Append(link.Url);
                    break;
                case Markdig.Syntax.CodeBlock block:
                    text.Append(" [").Append(LinesOf(block)).Append(']');
                    break;
                case HeadingBlock heading:
                    text.Append(" h").Append(heading.Level);
                    break;
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string LinesOf(Markdig.Syntax.CodeBlock block)
    {
        var lines = new List<string>();
        for (var i = 0; i < block.Lines.Count; i++) lines.Add(block.Lines.Lines[i].ToString());
        return string.Join("|", lines);
    }

    private static string Escape(string text) => text.Replace("\n", "\\n");

    /// <summary>A builder and a producer, pumped until the producer has caught up.</summary>
    private sealed class ProducerHost : IDisposable
    {
        private readonly ObservableStringBuilder builder = new();
        private readonly IDisposable subscription;
        private MarkdownDocumentUpdate? update;

        public MarkdownUpdateProducer Producer { get; }

        public MarkdownDocument Document => update?.Document ?? throw new InvalidOperationException("nothing parsed");

        public ProducerHost(bool incremental = true)
        {
            Producer = new MarkdownUpdateProducer { IncrementalParsing = incremental, MarkdownBuilder = builder };
            subscription = Producer.Subscribe(new Observer(value => update = value));
        }

        public void Append(string text)
        {
            builder.Append(text);

            var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 10);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                if (update is { } current && current.Version >= builder.Version) return;
                Thread.Sleep(0);
            }

            throw new TimeoutException("the producer never caught up with the builder");
        }

        public void Dispose() => subscription.Dispose();

        private sealed class Observer(Action<MarkdownDocumentUpdate> onNext) : IObserver<MarkdownDocumentUpdate>
        {
            public void OnCompleted() { }

            public void OnError(Exception error) => throw error;

            public void OnNext(MarkdownDocumentUpdate value) => onNext(value);
        }
    }
}
