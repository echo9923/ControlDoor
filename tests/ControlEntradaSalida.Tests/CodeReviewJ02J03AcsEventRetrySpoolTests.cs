using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 J2：死信源文件补清理成功后必须解除内存登记（图片字节引用可被释放）。
    // 代码复核 J3：磁盘回放达到预算即停止目录遍历；死信遗留补清理由独立 Patrol 巡检负责。
    public static class CodeReviewJ02J03AcsEventRetrySpoolTests
    {
        [TestCase]
        public static void AcsEventRetrySpool_DeadLetterLeftoverCleaned_ReleasesEventRegistrations()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            var requestIds = new[] { "j2-a", "j2-b", "j2-c" };
            foreach (var requestId in requestIds)
            {
                writer.Save(NewRawEvent(requestId, pictureBytes: 128 * 1024));
            }

            var sourcePaths = Directory.EnumerateFiles(retryDirectory, "*.json").ToArray();
            Assert.Equal(3, sourcePaths.Length);

            var processor = new RecordingProcessor();
            foreach (var requestId in requestIds)
            {
                processor.PoisonRequestIds.Add(requestId);
            }

            var service = NewService(retryDirectory, processor, batchSize: 3);
            service.ItemRetryLimit = 1;
            var context = new BackgroundTaskContext("j2-release", CancellationToken.None, null);

            // 锁住全部源文件（可读、不可删除）：重试耗尽进死信时源文件删除必定失败，登记保留。
            var lockStreams = sourcePaths.Select(path => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write)).ToList();
            try
            {
                service.StartAsync(context).GetAwaiter().GetResult();
                WaitUntil(() => Directory.Exists(deadLetterDirectory) && Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count() == 3, "三个事件未全部进入死信。");

                // 死信源文件删除失败期间登记保留（含图片字节引用），重启后也凭 dl- 配对不重放（复核 I3）。
                Assert.Equal(3, service.SpoolForTest.TrackedEventCount);

                // 解除占用：后续回放轮次补清理源文件，且必须同步解除登记（J2：引用归零）。
                foreach (var stream in lockStreams)
                {
                    stream.Dispose();
                }

                WaitUntil(() => service.SpoolForTest.TrackedEventCount == 0, "补清理成功后内存登记未解除，图片引用仍在滞留（J2）。");
                Assert.False(Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "补清理后源文件应删除。");
                Assert.Equal(3, Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count(), "补清理不得产生新的死信文件。");
            }
            finally
            {
                foreach (var stream in lockStreams)
                {
                    stream.Dispose();
                }

                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void AcsEventRetrySpool_LoadStopsAtBudget_DoesNotConsumeWholeDirectory()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            for (var index = 0; index < 20; index++)
            {
                writer.Save(NewRawEvent("j3-" + index));
            }

            // 新实例（模拟重启后空登记）：Load(5) 只取 5 个，其余文件原样保留在磁盘。
            var reader = new AcsEventRetrySpool(retryDirectory, null);
            var first = reader.Load(5);
            Assert.Equal(5, first.Count);
            Assert.Equal(20, Directory.EnumerateFiles(retryDirectory, "*.json").Count(), "预算外文件不得被消费或清理。");

            // 后续 Load 继续取未登记文件，不丢文件。
            var second = reader.Load(100);
            Assert.Equal(15, second.Count);
        }

        [TestCase]
        public static void AcsEventRetrySpool_Patrol_CleansLeftoversAndCountsDeadLettersIndependently()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            writer.Save(NewRawEvent("j3-leftover"));
            var sourcePath = Directory.EnumerateFiles(retryDirectory, "*.json").Single();

            // 人为制造死信配对（dl- 文件存在、源文件未清理）。
            Directory.CreateDirectory(deadLetterDirectory);
            File.WriteAllText(Path.Combine(deadLetterDirectory, "dl-" + Path.GetFileName(sourcePath)), "{}");

            // 新实例（模拟重启后空登记）：预算为 0 的 Load 不做清理，Patrol 负责补清理与计数。
            var restarted = new AcsEventRetrySpool(retryDirectory, null);
            var loaded = restarted.Load(0);
            Assert.Equal(0, loaded.Count);
            Assert.True(File.Exists(sourcePath), "Load(0) 不应触碰任何文件。");

            var patrol = restarted.Patrol(100);
            Assert.Equal(1, patrol.DeadLetterCount, "死信目录计数不正确。");
            Assert.Equal(1, patrol.LeftoverCleaned, "Patrol 未补清理死信遗留源文件。");
            Assert.False(File.Exists(sourcePath), "Patrol 补清理后源文件应删除。");
            Assert.Equal(0, restarted.TrackedEventCount);

            var secondPatrol = restarted.Patrol(100);
            Assert.Equal(1, secondPatrol.DeadLetterCount);
            Assert.Equal(0, secondPatrol.LeftoverCleaned, "已清理的遗留不得重复计数。");
        }

        [TestCase]
        public static void AcsEventRetrySpool_LoadWithSpareBudget_CleansLeftoverItVisits()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            writer.Save(NewRawEvent("j3-visit-leftover"));
            writer.Save(NewRawEvent("j3-normal"));
            var leftoverPath = Directory.EnumerateFiles(retryDirectory, "*.json")
                .First(path => File.ReadAllText(path).Contains("j3-visit-leftover"));

            Directory.CreateDirectory(deadLetterDirectory);
            File.WriteAllText(Path.Combine(deadLetterDirectory, "dl-" + Path.GetFileName(leftoverPath)), "{}");

            // 预算富余时，Load 访问到的遗留文件就地补清理并继续加载其余事件（兼容复核 I3 语义）。
            var reader = new AcsEventRetrySpool(retryDirectory, null);
            var loaded = reader.Load(10);
            Assert.Equal(1, loaded.Count, "遗留文件不得加载，普通事件应正常加载。");
            Assert.False(File.Exists(leftoverPath), "访问到的遗留源文件未就地清理。");
        }

        private static FaceEventIngestionService NewService(string retryDirectory, RecordingProcessor processor, int batchSize)
        {
            return new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 100, BatchSize = batchSize, FlushIntervalMs = 50 },
                processor,
                null,
                retryDirectory);
        }

        private static RawAcsAlarmEvent NewRawEvent(string requestId, int pictureBytes = 0)
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 9, 9, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                Source = AcsAlarmEventSource.Realtime,
                RequestId = requestId,
                PictureBytes = pictureBytes > 0 ? new byte[pictureBytes] : new byte[0]
            };
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(20);
            }

            Assert.True(condition(), message);
        }

        private sealed class RecordingProcessor : IAcsFaceEventProcessor
        {
            public readonly HashSet<string> PoisonRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public int PoisonAttempts;

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                return Decide(rawEvent);
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var rawEvent in rawEvents)
                {
                    var outcome = Decide(rawEvent);
                    results.Add(outcome.Success
                        ? FaceEventBatchItemResult.Ok(0, outcome.Code, outcome.Message)
                        : FaceEventBatchItemResult.Failed(0, outcome.Code, outcome.Message));
                }

                return results;
            }

            private FaceEventProcessResult Decide(RawAcsAlarmEvent rawEvent)
            {
                if (PoisonRequestIds.Contains(rawEvent.RequestId))
                {
                    PoisonAttempts++;
                    return FaceEventProcessResult.Failed("RETRYABLE_FAILURE", "transient poison");
                }

                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }
        }
    }
}
