using Avalonia.Headless;
using Avalonia.LogicalTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class MarkdownRendererStreamingTests
{
    private HeadlessUnitTestSession session = null!;

    [OneTimeSetUp]
    public void StartSession() => session = HeadlessSession.Current;

    [Test]
    public Task UpdatingOpenFenceInfo_ReplacesCodeBlockNodeWithMermaidBlockNode() => session.Dispatch(
        () =>
    {
        MarkdownNode.Register<MermaidBlockNode>();
        var pipeline = new MarkdownPipelineBuilder().UseMermaid().Build();
        var owner = new MarkdownRenderer();
        var documentNode = new DocumentNode(owner);

        var intermediateMarkdown =
            """
            ```m
            graph TD
                A --> B
            """;
        var mermaidMarkdown =
            """
            ```mermaid
            graph TD
                A --> B
            """;

        var intermediateDocument = Markdown.Parse(intermediateMarkdown, pipeline);
        var mermaidDocument = Markdown.Parse(mermaidMarkdown, pipeline);

        documentNode.Update(
            documentNode,
            intermediateDocument,
            new ObservableStringBuilderChangedEventArgs(0, intermediateMarkdown.Length, intermediateMarkdown.Length, 1),
            CancellationToken.None);

        Assert.That(documentNode.Control.GetLogicalDescendants().OfType<CodeBlock>(), Has.Exactly(1).Items);
        Assert.That(documentNode.Control.GetLogicalDescendants().OfType<MermaidPresenter>(), Is.Empty);

        documentNode.Update(
            documentNode,
            mermaidDocument,
            new ObservableStringBuilderChangedEventArgs(3, "ermaid".Length, mermaidMarkdown.Length, 2),
            CancellationToken.None);

        Assert.That(documentNode.Control.GetLogicalDescendants().OfType<CodeBlock>(), Is.Empty);
        Assert.That(documentNode.Control.GetLogicalDescendants().OfType<MermaidPresenter>(), Has.Exactly(1).Items);
    },
        CancellationToken.None);
}
