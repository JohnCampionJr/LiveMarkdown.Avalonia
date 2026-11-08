using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NUnit.Framework;

namespace LiveMarkdown.Avalonia.Tests;

/// <summary>Public-API-only probe, usable unchanged against the round-one source.</summary>
[TestFixture, NonParallelizable, Explicit("Fixed code-chip pointer workload; no timing gates")]
public class InlineHitTestBenchmark
{
    [Test]
    public async Task Repeated_pointer_queries()
    {
        var samples = await HeadlessSession.Current.Dispatch(() =>
        {
            var block = new MarkdownTextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
            var chips = Enumerable.Range(0, 40).Select(i => new CodeInline { Text = $"chip-{i:D2}-content" }).ToArray();
            foreach (var chip in chips) { block.Inlines!.Add(chip); block.Inlines.Add(new Run(" gap ")); }
            var window = new Window { Width = 320, Height = 600, Content = block };
            try
            {
                window.Show();
                window.UpdateLayout();
                var target = chips[^1];
                var point = block.GetCodeInlineRects(target)[0].Center;
                // Resolve all preceding chips before measuring the steady pointer path.
                Assert.That(block.GetCodeInlineAt(point), Is.SameAs(target));
                return Enumerable.Range(0, 7).Select(_ =>
                {
                    var hits = 0;
                    var rectangles = 0;
                    var bytes = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < 500; i++)
                    {
                        if (ReferenceEquals(block.GetCodeInlineAt(point), target)) hits++;
                        rectangles += block.GetCodeInlineRects(target).Count;
                    }
                    var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    Assert.That(hits, Is.EqualTo(500));
                    Assert.That(rectangles, Is.GreaterThanOrEqualTo(500));
                    return new { milliseconds, allocatedBytes, hits, rectangles };
                }).ToArray();
            }
            finally { window.Close(); }
        }, CancellationToken.None);
        var json = JsonSerializer.Serialize(new { workload = "chip-pointer-v1", chips = 40, width = 320,
            height = 600, queriesPerSample = 500, samples }, new JsonSerializerOptions { WriteIndented = true });
        TestContext.Out.WriteLine(json);
        if (Environment.GetEnvironmentVariable("LM_INLINE_HIT_BENCHMARK_OUTPUT") is { Length: > 0 } path)
            File.WriteAllText(path, json);
    }
}
