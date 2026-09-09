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
    // 代码复核 I2/I3：磁盘回放逐文件容错（部分读取失败不遗留登记）、死信转移幂等
    // （源文件删除失败不重复回放、不重复生成死信、重启后凭 dl- 配对文件识别遗留）。
    public static class CodeReviewI2I3AcsEventRetrySpoolTests
    {
        [TestCase]
        public static void AcsEventRetrySpool_LoadWithLockedFile_DeliversOthersAndRecoversWithoutRestart()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            writer.Save(NewRawEvent("i2-first"));
            writer.Save(NewRawEvent("i2-second"));

            // 文件名是随机 GUID，枚举顺序与保存顺序无关：按内容锁定 i2-second 的文件，
            // 保证 Load 先成功读取 i2-first，再在 i2-second 上遭遇 IOException。
            var files = Directory.EnumerateFiles(retryDirectory, "*.json").ToArray();
            Assert.Equal(2, files.Length);
            var lockedPath = files.First(p => File.ReadAllText(p).Contains("i2-second"));
            Assert.True(files.Any(p => p != lockedPath && File.ReadAllText(p).Contains("i2-first")), "两个事件文件内容不符合预期。");

            var processor = new RecordingProcessor();
            var service = NewService(retryDirectory, processor, batchSize: 2);
            var context = new BackgroundTaskContext("i2-load", CancellationToken.None, null);
            var lockStream = File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
            try
            {
                service.StartAsync(context).GetAwaiter().GetResult();

                // 第二个文件被占用：本轮已读取的第一个事件必须照常交付处理（旧实现登记后抛出，事件永不交付）。
                WaitUntil(() => processor.SuccessRequestIds.Contains("i2-first"), "部分读取失败时，已读取事件未交付处理。");

                // 持锁期间实时事件继续处理：单文件瞬时 IO 故障不得中断处理循环。
                Assert.True(service.TryEnqueue(NewRawEvent("i2-live")).Accepted);
                WaitUntil(() => processor.SuccessRequestIds.Contains("i2-live"), "文件占用期间实时事件未被处理。");

                // 解除占用后无需重启：第二个文件在后续 Load 轮次补加载处理。
                lockStream.Dispose();
                WaitUntil(() => processor.SuccessRequestIds.Contains("i2-second"), "解除占用后第二个事件未恢复处理。");
            }
            finally
            {
                lockStream.Dispose();
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }

            Assert.False(Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "全部处理完成后重试目录应清空。");
        }

        [TestCase]
        public static void AcsEventRetrySpool_DeadLetterSourceUndeletable_DoesNotReplayOrDuplicate()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            var poison = NewRawEvent("i3-poison");
            writer.Save(poison);
            var sourcePath = Directory.EnumerateFiles(retryDirectory, "*.json").Single();

            var processor = new RecordingProcessor();
            processor.PoisonRequestIds.Add(poison.RequestId);
            var service = NewService(retryDirectory, processor, batchSize: 2);
            service.ItemRetryLimit = 2;
            var context = new BackgroundTaskContext("i3-deadletter", CancellationToken.None, null);

            // 先锁住源文件（可读、不可删除）再启动：首次失败进重试道，重试耗尽进入死信时删除必定失败。
            var lockStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write);
            try
            {
                service.StartAsync(context).GetAwaiter().GetResult();

                WaitUntil(
                    () => Directory.Exists(deadLetterDirectory) && Directory.EnumerateFiles(deadLetterDirectory, "*.json").Any(),
                    "重试耗尽后死信文件未生成。");
                Assert.Equal(2, processor.PoisonAttempts, "重试上限为 2 时业务应恰好执行两次。");
                Assert.True(File.Exists(sourcePath), "死信源文件删除失败时应保留在磁盘。");
                var deadLetterJson = File.ReadAllText(Directory.EnumerateFiles(deadLetterDirectory, "*.json").First());
                Assert.Contains("RETRYABLE_FAILURE", deadLetterJson);

                // 稳定期观察：多个磁盘回放轮次后死信不重复、业务不重入、源文件保留待清理。
                Thread.Sleep(600);
                Assert.Equal(1, Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count(), "死信文件重复生成。");
                Assert.Equal(2, processor.PoisonAttempts, "死信后业务不得重新入处理。");
                Assert.True(File.Exists(sourcePath), "占用未解除前源文件应保留。");
                Assert.Equal(0, service.Count);

                // 解除占用：后续 Load 轮次凭 dl- 配对文件补清理源文件，死信份数不变。
                lockStream.Dispose();
                WaitUntil(() => !File.Exists(sourcePath), "解除占用后死信遗留源文件未补清理。");
                Assert.Equal(1, Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count(), "补清理不应产生新的死信文件。");
                Assert.Equal(2, processor.PoisonAttempts, "补清理不得触发业务重放。");
            }
            finally
            {
                lockStream.Dispose();
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void AcsEventRetrySpool_DeadLetteredOrphanAfterRestart_IsSkippedAndCleaned()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            var item = NewRawEvent("i3-orphan");
            writer.Save(item);
            var sourcePath = Directory.EnumerateFiles(retryDirectory, "*.json").Single();

            var lockStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write);
            try
            {
                // 重复死信两次：目标名与源文件绑定，死信文件恒为 1 份。
                writer.DeadLetter(item, "RETRYABLE_FAILURE", "exhausted");
                writer.DeadLetter(item, "RETRYABLE_FAILURE", "exhausted");
                Assert.Equal(1, Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count(), "死信转移不幂等，产生了重复死信。");
                Assert.True(File.Exists(sourcePath), "源文件删除失败时应保留。");

                // 模拟重启：新 spool 实例内存登记为空，遗留源文件不得重新投递业务。
                var restarted = new AcsEventRetrySpool(retryDirectory, null);
                var loaded = restarted.Load(10);
                Assert.Equal(0, loaded.Count, "重启后死信遗留源文件不得重新加载。");

                // 解除占用后下一轮 Load 补清理，仍不投递。
                lockStream.Dispose();
                var loadedAgain = restarted.Load(10);
                Assert.Equal(0, loadedAgain.Count, "补清理轮次不得投递已死信事件。");
                Assert.False(File.Exists(sourcePath), "解除占用后遗留源文件未补清理。");
                Assert.Equal(1, Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count());
            }
            finally
            {
                lockStream.Dispose();
            }
        }

        private static FaceEventIngestionService NewService(string retryDirectory, RecordingProcessor processor, int batchSize)
        {
            return new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 100, BatchSize = batchSize, FlushIntervalMs = 50 },
                processor,
                null,
                retryDirectory);
        }

        private static RawAcsAlarmEvent NewRawEvent(string requestId)
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 9, 7, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                Source = AcsAlarmEventSource.Realtime,
                RequestId = requestId
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
            public readonly HashSet<string> SuccessRequestIds = new HashSet<string>(StringComparer.Ordinal);
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

                SuccessRequestIds.Add(rawEvent.RequestId);
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }
        }
    }
}
