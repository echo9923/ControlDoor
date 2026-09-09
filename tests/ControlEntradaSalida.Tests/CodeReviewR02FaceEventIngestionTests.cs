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
    // 代码复核 R02/R04：永久失败事件不得占住活动批次；队列有积压时消费不得被逐条等待限速。
    public static class CodeReviewR02FaceEventIngestionTests
    {
        [TestCase]
        public static void FaceEventIngestionService_PoisonEvent_DoesNotBlockHealthyEvents()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new ScriptedProcessor { RetryablePoison = false };
            var service = NewService(retryDirectory, processor, batchSize: 10);
            // 复核 L1/L2：数据库签名失败改为有界重试后耗尽上限才死信；注入小上限保持用例快速收敛。
            service.ItemRetryLimit = 2;
            var context = new BackgroundTaskContext("r02-poison", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                var poison = NewRawEvent("r02-poison");
                processor.PoisonRequestIds.Add(poison.RequestId);
                Assert.True(service.TryEnqueue(poison).Accepted);
                for (var i = 0; i < 3; i++)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent("r02-healthy-" + i)).Accepted);
                }

                WaitUntil(() => processor.SuccessCount >= 3, "健康事件未被处理，毒事件阻塞了批次。");
                var deadLetterDir = Path.Combine(retryDirectory, "dead-letter");
                WaitUntil(() => Directory.Exists(deadLetterDir) && Directory.EnumerateFiles(deadLetterDir, "*.json").Any(), "毒事件未进入死信区。");

                var deadLetterJson = File.ReadAllText(Directory.EnumerateFiles(deadLetterDir, "*.json").First());
                Assert.Contains("DATABASE_FAILURE", deadLetterJson);
                Assert.False(Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "死信后不应残留重试文件。");
                Assert.Equal(0, service.Count);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_UniformDatabaseFailure_BacksoffInsteadOfDeadLetter()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new ScriptedProcessor { RetryablePoison = false, FailAll = true };
            var service = NewService(retryDirectory, processor, batchSize: 10);
            var context = new BackgroundTaskContext("r02-env", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent("r02-env-" + i)).Accepted);
                }

                // 整轮以同一非重试错误失败按环境故障退避：事件应进入重试道而不是立即死信。
                WaitUntil(() => Directory.EnumerateFiles(retryDirectory, "*.json").Count() >= 2, "环境故障下事件未进入重试道。");
                var deadLetterDir = Path.Combine(retryDirectory, "dead-letter");
                Assert.False(Directory.Exists(deadLetterDir) && Directory.EnumerateFiles(deadLetterDir, "*.json").Any(), "环境故障不应立即死信。");

                // 队列未被堵死：后续新事件仍可入队。
                Assert.True(service.TryEnqueue(NewRawEvent("r02-env-extra")).Accepted);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_RetryAttemptsExhausted_MovesToDeadLetter()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new ScriptedProcessor { RetryablePoison = true };
            var service = NewService(retryDirectory, processor, batchSize: 5);
            service.ItemRetryLimit = 2;
            var context = new BackgroundTaskContext("r02-cap", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                var poison = NewRawEvent("r02-cap-poison");
                processor.PoisonRequestIds.Add(poison.RequestId);
                Assert.True(service.TryEnqueue(poison).Accepted);
                var healthy = NewRawEvent("r02-cap-healthy");
                Assert.True(service.TryEnqueue(healthy).Accepted);

                var deadLetterDir = Path.Combine(retryDirectory, "dead-letter");
                WaitUntil(() => Directory.Exists(deadLetterDir) && Directory.EnumerateFiles(deadLetterDir, "*.json").Any(), "重试耗尽后事件未进入死信区。");
                WaitUntil(() => processor.SuccessCount >= 1, "健康事件未被处理。");
                Assert.True(processor.PoisonAttempts >= 2, "重试上限未生效。");
                Assert.Equal(0, service.Count);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_PrefilledQueue_DrainsWithoutPerItemWait()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new ScriptedProcessor { TrackBatchSizes = true };
            var service = NewService(retryDirectory, processor, batchSize: 50);
            var context = new BackgroundTaskContext("r04-drain", CancellationToken.None, null);
            for (var i = 0; i < 100; i++)
            {
                Assert.True(service.TryEnqueue(NewRawEvent("r04-" + i)).Accepted);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                WaitUntil(() => processor.SuccessCount >= 100, "预填事件未被及时消费。");
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 2000, "积压消费耗时异常：" + watch.ElapsedMilliseconds + "ms");
                Assert.Equal(50, processor.MaxBatchSize);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_RestartRestoresPendingFromSpool()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var failing = new ScriptedProcessor { RetryablePoison = true };
            var first = NewService(retryDirectory, failing, batchSize: 5);
            var context = new BackgroundTaskContext("r02-restore", CancellationToken.None, null);
            first.StartAsync(context).GetAwaiter().GetResult();
            for (var i = 0; i < 2; i++)
            {
                var pending = NewRawEvent("r02-restore-" + i);
                failing.PoisonRequestIds.Add(pending.RequestId);
                Assert.True(first.TryEnqueue(pending).Accepted);
            }

            WaitUntil(() => Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "失败事件未持久化到重试文件。");
            first.StopAsync(context).GetAwaiter().GetResult();
            first.Dispose();

            Assert.True(Directory.EnumerateFiles(retryDirectory, "*.json").Count() >= 2, "停止后重试文件缺失。");

            var recovered = new ScriptedProcessor();
            var second = NewService(retryDirectory, recovered, batchSize: 5);
            second.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                WaitUntil(() => recovered.SuccessCount >= 2, "重启后未恢复待处理事件。");
                WaitUntil(() => !Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "恢复完成后重试文件未清理。");
            }
            finally
            {
                second.StopAsync(context).GetAwaiter().GetResult();
                second.Dispose();
            }
        }

        private static FaceEventIngestionService NewService(string retryDirectory, ScriptedProcessor processor, int batchSize)
        {
            return new FaceEventIngestionService(
                new FaceEventLoggingOptions { QueueCapacity = 100, BatchSize = batchSize, FlushIntervalMs = 200 },
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
            var deadline = DateTime.UtcNow.AddSeconds(5);
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

        private sealed class ScriptedProcessor : IAcsFaceEventProcessor
        {
            public readonly HashSet<string> PoisonRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public bool RetryablePoison;
            public bool FailAll;
            public bool TrackBatchSizes;
            public int SuccessCount;
            public int PoisonAttempts;
            public int MaxBatchSize;

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                return Decide(rawEvent);
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                if (TrackBatchSizes && rawEvents.Count > MaxBatchSize)
                {
                    MaxBatchSize = rawEvents.Count;
                }

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
                var poison = PoisonRequestIds.Contains(rawEvent.RequestId);
                if (poison)
                {
                    PoisonAttempts++;
                    return RetryablePoison
                        ? FaceEventProcessResult.Failed("RETRYABLE_FAILURE", "transient poison")
                        : FaceEventProcessResult.Failed("DATABASE_FAILURE", "constraint violation");
                }

                if (FailAll)
                {
                    return FaceEventProcessResult.Failed("DATABASE_FAILURE", "table missing");
                }

                SuccessCount++;
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }
        }
    }
}
