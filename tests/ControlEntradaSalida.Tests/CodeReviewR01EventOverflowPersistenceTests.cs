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
    // 代码复核 R1：内存队列满时事件不得直接丢弃——经有界溢出通道异步落盘到现有重试目录，
    // 恢复消费后由磁盘回放自动补齐；溢出通道也满才返回 OVERFLOW_FULL（文档化最后边界）。
    public static class CodeReviewR01EventOverflowPersistenceTests
    {
        [TestCase]
        public static void FaceEventIngestion_QueueFull_PersistsViaOverflowAndReplaysAfterRecovery()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new GatedProcessor();
            var service = NewService(retryDirectory, processor, queueCapacity: 2, overflowCapacity: 50, batchSize: 2);
            var context = new BackgroundTaskContext("r01-overflow", CancellationToken.None, null);
            try
            {
                // 先送哨兵事件并等处理器进入门内：此后消费者阻塞在批次里，不再腾空内存队列，
                // 后续突发入队的结果才是确定性的（2 条进队列、其余进溢出通道）。
                processor.Gate.Close();
                service.StartAsync(context).GetAwaiter().GetResult();
                Assert.True(service.TryEnqueue(NewRawEvent("r01-sentinel")).Accepted);
                WaitUntil(() => processor.Entered.IsSet, "哨兵事件未进入处理器。");

                var results = new List<FaceEventEnqueueResult>();
                for (var index = 0; index < 10; index++)
                {
                    results.Add(service.TryEnqueue(NewRawEvent("r01-burst-" + index)));
                }

                // 前 2 条进内存队列，其余全部经溢出通道落盘，一条不丢。
                Assert.Equal(2, results.Count(result => result.Accepted && result.Code == "OK"));
                Assert.Equal(8, results.Count(result => result.Accepted && result.Code == "QUEUE_FULL_PERSISTED"));
                Assert.False(results.Any(result => !result.Accepted), "队列满期间不得丢弃事件。");
                WaitUntil(
                    () => Directory.Exists(retryDirectory) && Directory.EnumerateFiles(retryDirectory, "*.json").Count() >= 8,
                    "溢出事件未全部落盘。");

                // 恢复消费：哨兵 + 内存队列 2 条 + 磁盘回放 8 条全部处理完成。
                processor.Gate.Open();
                var expected = new List<string> { "r01-sentinel" };
                expected.AddRange(Enumerable.Range(0, 10).Select(index => "r01-burst-" + index));
                WaitUntil(() => expected.All(id => processor.SuccessRequestIds.Contains(id)), "恢复消费后未补齐全部溢出事件。");

                WaitUntil(() => !Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "处理完成后重试目录应清空。");
            }
            finally
            {
                processor.Gate.Open();
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestion_OverflowLaneFull_RejectsWithOverflowFull()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new RecordingProcessor();
            // 不启动后台线程（消费者与写盘线程都缺席）：溢出通道写盘线程速度远快于入队，
            // 只有它不运行时"队列满 + 溢出通道也满"才是确定性的（OVERFLOW_FULL 最后边界）。
            var service = NewService(retryDirectory, processor, queueCapacity: 2, overflowCapacity: 1, batchSize: 2);

            Assert.True(service.TryEnqueue(NewRawEvent("r01-full-a")).Accepted);
            Assert.True(service.TryEnqueue(NewRawEvent("r01-full-b")).Accepted);
            Assert.True(service.TryEnqueue(NewRawEvent("r01-full-c")).Accepted);
            var rejected = service.TryEnqueue(NewRawEvent("r01-full-d"));
            Assert.False(rejected.Accepted);
            Assert.Equal("OVERFLOW_FULL", rejected.Code);

            service.StopAsync(new BackgroundTaskContext("r01-overflow-full-stop", CancellationToken.None, null)).GetAwaiter().GetResult();
            service.Dispose();
            // 溢出通道内的事件仍被停止兜底落盘。
            Assert.Equal(3, Directory.EnumerateFiles(retryDirectory, "*.json").Count());
        }

        [TestCase]
        public static void FaceEventIngestion_StopWithoutStart_PersistsOverflowLaneToDisk()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new RecordingProcessor();
            // 不启动后台线程（模拟服务未就绪/立即停止的场合）：PersistPending 仍需兜底落盘溢出通道。
            var service = NewService(retryDirectory, processor, queueCapacity: 2, overflowCapacity: 50, batchSize: 2);

            Assert.True(service.TryEnqueue(NewRawEvent("r01-stop-a")).Accepted);
            Assert.True(service.TryEnqueue(NewRawEvent("r01-stop-b")).Accepted);
            var overflow = service.TryEnqueue(NewRawEvent("r01-stop-c"));
            Assert.True(overflow.Accepted);
            Assert.Equal("QUEUE_FULL_PERSISTED", overflow.Code);

            service.StopAsync(new BackgroundTaskContext("r01-stop", CancellationToken.None, null)).GetAwaiter().GetResult();
            service.Dispose();
            Assert.Equal(3, Directory.EnumerateFiles(retryDirectory, "*.json").Count(), "停止兜底应把队列与溢出通道内的事件全部落盘。");
        }

        private static FaceEventIngestionService NewService(string retryDirectory, IAcsFaceEventProcessor processor, int queueCapacity, int overflowCapacity, int batchSize)
        {
            return new FaceEventIngestionService(
                new FaceEventLoggingOptions
                {
                    QueueCapacity = queueCapacity,
                    OverflowQueueCapacity = overflowCapacity,
                    BatchSize = batchSize,
                    FlushIntervalMs = 50
                },
                processor,
                null,
                retryDirectory);
        }

        private static RawAcsAlarmEvent NewRawEvent(string requestId)
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 9, 9, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                Source = AcsAlarmEventSource.Realtime,
                RequestId = requestId
            };
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
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

        private sealed class Gate
        {
            private readonly ManualResetEventSlim open = new ManualResetEventSlim(true);

            public void Close()
            {
                open.Reset();
            }

            public void Open()
            {
                open.Set();
            }

            public bool Wait()
            {
                return open.Wait(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class GatedProcessor : IAcsFaceEventProcessor
        {
            public readonly Gate Gate = new Gate();
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public readonly HashSet<string> SuccessRequestIds = new HashSet<string>(StringComparer.Ordinal);

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                Entered.Set();
                Gate.Wait();
                SuccessRequestIds.Add(rawEvent.RequestId);
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var rawEvent in rawEvents)
                {
                    var outcome = Process(rawEvent);
                    results.Add(FaceEventBatchItemResult.Ok(0, outcome.Code, outcome.Message));
                }

                return results;
            }
        }

        private sealed class RecordingProcessor : IAcsFaceEventProcessor
        {
            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                return rawEvents.Select(item => FaceEventBatchItemResult.Ok(0, "INSERTED", "ok")).ToList();
            }
        }
    }
}
