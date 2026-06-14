using System;
using System.Collections.Generic;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;
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
    }
}
