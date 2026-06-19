using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7FaceEventIngestionQueueTests
    {
        [TestCase]
        public static void FaceEventIngestionService_NullEnqueue_ReturnsInvalidArgument()
        {
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions());

            var result = service.TryEnqueue(null);

            Assert.False(result.Accepted);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
            service.Dispose();
        }

        [TestCase]
        public static void FaceEventIngestionService_FullQueue_ReturnsQueueFull()
        {
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 2 });

            var first = service.TryEnqueue(NewRawEvent());
            var second = service.TryEnqueue(NewRawEvent());
            var third = service.TryEnqueue(NewRawEvent());

            Assert.True(first.Accepted);
            Assert.True(second.Accepted);
            Assert.False(third.Accepted);
            Assert.Equal("QUEUE_FULL", third.Code);
            Assert.Equal(2, service.Capacity);
            Assert.Equal(2, service.Count);
            service.Dispose();
        }

        [TestCase]
        public static void FaceEventIngestionService_AfterStop_ReturnsStopped()
        {
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions());
            var context = new BackgroundTaskContext("stage7-queue-test", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            service.StopAsync(context).GetAwaiter().GetResult();

            var result = service.TryEnqueue(NewRawEvent());

            Assert.False(result.Accepted);
            Assert.Equal("STOPPED", result.Code);
            service.Dispose();
        }

        [TestCase]
        public static void FaceEventIngestionService_AfterDispose_ReturnsStopped()
        {
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions());

            service.Dispose();

            var result = service.TryEnqueue(NewRawEvent());

            Assert.False(result.Accepted);
            Assert.Equal("STOPPED", result.Code);
        }

        [TestCase]
        public static void FaceEventIngestionService_WorkerSurvivesProcessorException()
        {
            var processor = new ThrowingThenRecordingProcessor();
            // BatchSize=1 强制每条独立成批，确保第一条抛异常不会把第二条拖进同一批，恢复原"逐条"语义。
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 10, BatchSize = 1, FlushIntervalMs = 50 }, processor);
            var context = new BackgroundTaskContext("stage7-queue-test", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                var first = service.TryEnqueue(NewRawEvent());
                var second = service.TryEnqueue(NewRawEvent());

                Assert.True(first.Accepted);
                Assert.True(second.Accepted);

                WaitUntil(() => processor.SuccessCount >= 1, "second ACS event was not processed after the first threw.");

                Assert.True(processor.ThrownOnce);
                Assert.Equal(1, processor.SuccessCount);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_StopAsync_DrainsAcceptedEventsBeforeReturning()
        {
            var processor = new BlockingRecordingProcessor();
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 20, BatchSize = 1, FlushIntervalMs = 5000 }, processor);
            var context = new BackgroundTaskContext("stage7-drain-stop-test", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                for (var i = 0; i < 5; i++)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent()).Accepted);
                }

                Assert.True(processor.WaitForFirstItem(2000), "first accepted ACS event was not picked up.");
                var stopTask = service.StopAsync(context);
                Thread.Sleep(100);
                Assert.False(stopTask.IsCompleted, "StopAsync returned before the in-flight event was released.");

                processor.Release();
                stopTask.GetAwaiter().GetResult();

                Assert.Equal(5, processor.ProcessedCount);
                Assert.Equal(0, service.Count);
            }
            finally
            {
                processor.Release();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_StopAsync_TimeoutLogsUnfinishedAcceptedEvents()
        {
            var runDirectory = TestWorkspace.Create();
            using (var logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" })))
            {
                var processor = new NeverReleasingProcessor();
                var service = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 20, BatchSize = 1, FlushIntervalMs = 5000 }, processor, logger);
                var context = new BackgroundTaskContext("stage7-drain-timeout-test", CancellationToken.None, logger);
                service.StartAsync(context).GetAwaiter().GetResult();
                try
                {
                    Assert.True(service.TryEnqueue(NewRawEvent()).Accepted);
                    Assert.True(processor.WaitForFirstItem(2000), "accepted ACS event was not picked up.");

                    service.StopAsync(context).GetAwaiter().GetResult();

                    var text = File.ReadAllText(logger.CurrentLogPath);
                    Assert.Contains("ACS event ingestion stop timed out before queue drain completed.", text);
                    Assert.Contains("unfinishedAccepted=\"1\"", text);
                    Assert.Contains("queueDepth=\"0\"", text);
                }
                finally
                {
                    service.Dispose();
                }
            }
        }

        private static RawAcsAlarmEvent NewRawEvent()
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 6, 13, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                Source = AcsAlarmEventSource.Realtime,
                RequestId = Guid.NewGuid().ToString("N")
            };
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
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

        private sealed class ThrowingThenRecordingProcessor : IAcsFaceEventProcessor
        {
            private int invocations;
            private bool thrown;

            public bool ThrownOnce => thrown;

            public int SuccessCount { get; private set; }

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                invocations++;
                if (invocations == 1)
                {
                    thrown = true;
                    throw new InvalidOperationException("processor exploded on first event");
                }

                SuccessCount++;
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                // 复用单条 Process 的"第一条抛异常"语义，保证 worker 异常存活测试在新攒批路径下仍有效。
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var rawEvent in rawEvents)
                {
                    var result = Process(rawEvent);
                    results.Add(FaceEventBatchItemResult.Ok(0, result.Code, result.Message));
                }
                return results;
            }
        }

        private sealed class BlockingRecordingProcessor : IAcsFaceEventProcessor
        {
            private readonly ManualResetEventSlim firstItem = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
            private int processedCount;

            public int ProcessedCount => processedCount;

            public bool WaitForFirstItem(int timeoutMs)
            {
                return firstItem.Wait(timeoutMs);
            }

            public void Release()
            {
                release.Set();
            }

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                firstItem.Set();
                release.Wait();
                Interlocked.Increment(ref processedCount);
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var rawEvent in rawEvents)
                {
                    var result = Process(rawEvent);
                    results.Add(FaceEventBatchItemResult.Ok(0, result.Code, result.Message));
                }
                return results;
            }
        }

        private sealed class NeverReleasingProcessor : IAcsFaceEventProcessor
        {
            private readonly ManualResetEventSlim firstItem = new ManualResetEventSlim(false);

            public bool WaitForFirstItem(int timeoutMs)
            {
                return firstItem.Wait(timeoutMs);
            }

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                firstItem.Set();
                Thread.Sleep(Timeout.Infinite);
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                firstItem.Set();
                Thread.Sleep(Timeout.Infinite);
                return new List<FaceEventBatchItemResult>();
            }
        }
    }
}
