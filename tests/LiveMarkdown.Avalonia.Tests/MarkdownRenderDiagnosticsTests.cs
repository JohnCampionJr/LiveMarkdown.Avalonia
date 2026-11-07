using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Markdig;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

[TestFixture]
[NonParallelizable]
public class MarkdownRenderDiagnosticsTests
{
    private sealed class Clock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _now;
        public void Spend(int milliseconds) => _now += milliseconds;
    }

    [Test]
    public void Nested_phase_times_are_exclusive_and_sum_to_the_update_total()
    {
        var clock = new Clock();
        var metrics = new MarkdownRenderMetrics(clock, "full", 10);
        clock.Spend(2);
        using (metrics.Measure(MarkdownRenderPhase.Blocks))
        {
            clock.Spend(3);
            using (metrics.Measure(MarkdownRenderPhase.Inlines))
            {
                clock.Spend(5);
                using (metrics.Measure(MarkdownRenderPhase.TreeChanges)) clock.Spend(7);
                clock.Spend(11);
            }
            clock.Spend(13);
        }
        var report = metrics.Finish();
        Assert.Multiple(() =>
        {
            Assert.That(report.Blocks, Is.EqualTo(TimeSpan.FromMilliseconds(16)));
            Assert.That(report.Inlines, Is.EqualTo(TimeSpan.FromMilliseconds(16)));
            Assert.That(report.TreeChanges, Is.EqualTo(TimeSpan.FromMilliseconds(7)));
            Assert.That(report.Other, Is.EqualTo(TimeSpan.FromMilliseconds(2)));
            Assert.That(report.Total, Is.EqualTo(TimeSpan.FromMilliseconds(41)));
        });
    }

    [Test]
    public void Slow_reports_use_the_injected_clock_and_nested_captures_do_not_double_count()
    {
        var reports = new Reports();
        using var subscription = MarkdownRenderDiagnostics.Listener.Subscribe(reports, name => name == MarkdownRenderDiagnostics.SlowUpdateEvent);
        var clock = new Clock();
        using (var capture = MarkdownRenderDiagnostics.Begin("fast", 1, clock))
        {
            clock.Spend(1);
            capture.Complete();
        }
        Assert.That(reports.Values, Is.Empty);
        using (var capture = MarkdownRenderDiagnostics.Begin("full", 10, clock))
        {
            using (var nested = MarkdownRenderDiagnostics.Begin("nested", 20, clock))
            {
                clock.Spend(2);
                nested.Complete();
            }
            capture.Complete();
        }
        Assert.Multiple(() =>
        {
            Assert.That(reports.Values, Has.Count.EqualTo(1));
            Assert.That(reports.Values[0].Kind, Is.EqualTo("full"));
            Assert.That(reports.Values[0].Total, Is.EqualTo(TimeSpan.FromMilliseconds(2)));
            Assert.That(reports.Values[0].Characters, Is.EqualTo(10));
        });
    }

    [Test]
    public async Task Reports_distinguish_document_rebinding_from_same_document_reuse_and_preserve_rendered_text()
    {
        await HeadlessSession.Current.Dispatch(() =>
        {
            var reports = new Reports();
            using var subscription = MarkdownRenderDiagnostics.Listener.Subscribe(reports, name => name == MarkdownRenderDiagnostics.UpdateEvent);
            var renderer = new MarkdownRenderer();
            var first = new MarkdownDocumentUpdate.Full(Markdown.Parse("private source **bold**\n\nsecond paragraph"));
            renderer.DocumentUpdate = first;
            Assert.That(reports.Values, Has.Count.EqualTo(1));
            var report = reports.Values[0];
            Assert.Multiple(() =>
            {
                Assert.That(report.Completed, Is.True);
                Assert.That(report.Kind, Is.EqualTo("full"));
                Assert.That(report.BlockUpdates, Is.GreaterThan(0));
                Assert.That(report.InlineUpdates, Is.GreaterThan(0));
                Assert.That(report.ConstructedNodes, Is.GreaterThan(0));
                Assert.That(report.CollectionChanges, Is.GreaterThan(0));
                Assert.That(report.ToString(), Does.Not.Contain("private source"));
            });
            var tree = renderer.GetVisualChildren().Single();
            renderer.DocumentUpdate = first;
            Assert.That(reports.Values, Has.Count.EqualTo(1), "same-document assignments perform no update");
            Assert.That(renderer.GetVisualChildren().Single(), Is.SameAs(tree));
            renderer.DocumentUpdate = new MarkdownDocumentUpdate.Full(Markdown.Parse("different **reply**"));
            Assert.That(reports.Values, Has.Count.EqualTo(2));
            renderer.DocumentUpdate = null;
            Assert.That(reports.Values[^1].Kind, Is.EqualTo("clear"));
            Assert.That(MarkdownRenderDiagnostics.Current, Is.Null);
        }, CancellationToken.None);
    }

    [Test]
    public void Disabled_capture_has_no_accumulator_and_exception_unwinding_resets_the_active_scope()
    {
        using (MarkdownRenderDiagnostics.Begin("disabled", 0)) Assert.That(MarkdownRenderDiagnostics.Current, Is.Null);
        var reports = new Reports();
        using var subscription = MarkdownRenderDiagnostics.Listener.Subscribe(reports, name => name == MarkdownRenderDiagnostics.UpdateEvent);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var capture = MarkdownRenderDiagnostics.Begin("failed", 0);
            using var phase = MarkdownRenderDiagnostics.Measure(MarkdownRenderPhase.Inlines);
            throw new InvalidOperationException("test");
        });
        Assert.That(MarkdownRenderDiagnostics.Current, Is.Null);
        Assert.That(reports.Values.Single().Completed, Is.False);
    }

    private sealed class Reports : IObserver<KeyValuePair<string, object?>>
    {
        public List<MarkdownRenderReport> Values { get; } = [];
        public void OnNext(KeyValuePair<string, object?> value) => Values.Add((MarkdownRenderReport)value.Value!);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }
}
