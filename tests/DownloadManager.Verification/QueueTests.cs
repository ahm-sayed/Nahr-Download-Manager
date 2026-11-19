using DownloadManager.Core.Models;
using DownloadManager.Engine;

namespace DownloadManager.Verification;

public static class QueueTests
{
    public static IEnumerable<TestCase> All()
    {
        yield return new TestCase("queue: dispatches FIFO when capacity is released", async _ =>
        {
            using var queue = new QueueManager(1);
            queue.SetPaused(true);

            var items = Enumerable.Range(1, 3).Select(i => NewItem($"fifo{i}.bin")).ToArray();
            var waiting = items.Select(item => queue.WaitAsync(item)).ToArray();
            Assert.Equal(3, queue.PendingCount, "three waiters queued while paused");
            Assert.Equal(false, waiting.Any(task => task.IsCompleted), "paused queue grants no leases");

            queue.SetPaused(false);
            using (var first = await waiting[0])
            {
                Assert.Equal(2, queue.PendingCount, "two waiters remain after first grant");
                Assert.Equal(1, queue.ActiveCount, "one lease active");
            }

            using (var second = await waiting[1])
            {
                Assert.Equal(1, queue.PendingCount, "one waiter remains after second grant");
            }
            using (var third = await waiting[2])
            {
                Assert.Equal(0, queue.PendingCount, "queue drained");
            }
            Assert.Equal(0, queue.ActiveCount, "all leases released");
        });

        yield return new TestCase("queue: priority reorders waiting items", async _ =>
        {
            using var queue = new QueueManager(1);
            queue.SetPaused(true);
            var firstItem = NewItem("first.bin");
            var secondItem = NewItem("second.bin");
            var thirdItem = NewItem("third.bin");
            var first = queue.WaitAsync(firstItem);
            var second = queue.WaitAsync(secondItem);
            var third = queue.WaitAsync(thirdItem);

            queue.SetPriority(firstItem.Id, -1);
            queue.SetPriority(thirdItem.Id, 5);
            queue.SetPaused(false);

            using (await third) { }
            using (await second) { }
            using (await first) { }
            Assert.Equal(0, queue.PendingCount, "all priority-ordered waiters dispatched");
        });

        yield return new TestCase("queue: category limit serializes matching items", async _ =>
        {
            using var queue = new QueueManager(3);
            queue.SetCategoryLimit("images", 1);
            queue.SetPaused(true);
            var firstItem = NewItem("one.png", "images");
            var secondItem = NewItem("two.png", "images");
            var otherItem = NewItem("document.pdf", "documents");
            var first = queue.WaitAsync(firstItem);
            var second = queue.WaitAsync(secondItem);
            var other = queue.WaitAsync(otherItem);

            queue.SetPaused(false);
            using (await first)
            {
                using (await other) { }
                Assert.Equal(false, second.IsCompleted, "same-category waiter waits for lease release");
            }
            using (await second) { }
            Assert.Equal(0, queue.ActiveCount, "all category leases released");
        });

        yield return new TestCase("queue: scheduled waiter starts only after its time", async env =>
        {
            using var queue = new QueueManager(1);
            queue.SetPaused(true);
            var immediateItem = NewItem("immediate.bin");
            var scheduledItem = NewItem("scheduled.bin");
            var immediate = queue.WaitAsync(immediateItem);
            var scheduled = queue.WaitAsync(scheduledItem);
            queue.SetScheduledStart(scheduledItem.Id, DateTimeOffset.UtcNow.AddMilliseconds(150));
            queue.SetPaused(false);

            using (await immediate) { }
            Assert.Equal(false, scheduled.IsCompleted, "scheduled item remains pending before its time");
            using (await scheduled.WaitAsync(TimeSpan.FromSeconds(3), env.Ct)) { }
            Assert.Equal(0, queue.PendingCount, "scheduled queue drained");
        });

        yield return new TestCase("queue: pause blocks dispatch and resume grants capacity", async _ =>
        {
            using var queue = new QueueManager(2);
            queue.SetPaused(true);
            var waiting = queue.WaitAsync(NewItem("paused.bin"));
            Assert.Equal(1, queue.PendingCount, "waiter stays pending while paused");
            Assert.Equal(false, waiting.IsCompleted, "paused queue grants no lease");

            queue.SetPaused(false);
            using (await waiting) { }
            Assert.Equal(0, queue.ActiveCount, "lease is released");
        });

        yield return new TestCase("queue: global concurrency limit is enforced and adjustable", async _ =>
        {
            using var queue = new QueueManager(1);
            queue.SetPaused(true);
            var tasks = Enumerable.Range(1, 3).Select(i => queue.WaitAsync(NewItem($"limit{i}.bin"))).ToArray();
            queue.SetPaused(false);

            var first = await tasks[0];
            Assert.Equal(false, tasks[1].IsCompleted, "second waiter blocked at limit one");
            queue.SetMaxConcurrent(2);
            var second = await tasks[1];
            Assert.Equal(false, tasks[2].IsCompleted, "third waiter blocked at limit two");
            first.Dispose();
            var third = await tasks[2];
            second.Dispose();
            third.Dispose();
            Assert.Equal(0, queue.ActiveCount, "all leases released");
        });

        yield return new TestCase("queue: cancellation removes a pending waiter", async env =>
        {
            using var queue = new QueueManager(1);
            queue.SetPaused(true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(env.Ct);
            var waiting = queue.WaitAsync(NewItem("cancelled.bin"), cts.Token);
            Assert.Equal(1, queue.PendingCount, "waiter added");
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting, "cancel pending queue wait");
            Assert.Equal(0, queue.PendingCount, "cancelled waiter removed");
        });
    }

    private static DownloadItem NewItem(string fileName, string category = "General") => new()
    {
        FileName = fileName,
        FilePath = Path.Combine(Path.GetTempPath(), fileName),
        Category = category,
    };
}
