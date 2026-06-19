using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7FaceEventBatchTests
    {
        // ===== Repository 批量逻辑 =====

        [TestCase]
        public static void FaceEventRepository_InsertEvents_EmptyList_ReturnsEmpty()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var repository = new FaceEventRepository(database, storage);

            var results = repository.InsertEvents(new List<AcsFaceEvent>(0));

            Assert.True(results != null && results.Count == 0);
        }

        [TestCase]
        public static void FaceEventRepository_InsertEvents_InvalidEvents_ReturnsInvalidWithoutDb()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var repository = new FaceEventRepository(database, storage);

            var results = repository.InsertEvents(new List<AcsFaceEvent>
            {
                new AcsFaceEvent { EventId = 0, EmployeeId = "u1", EventTime = DateTime.Now },
                null
            });

            Assert.Equal(2, results.Count);
            Assert.Equal(FaceEventInsertStatus.Invalid, results[0].Status);
            Assert.Equal(FaceEventInsertStatus.Invalid, results[1].Status);
            // 校验失败不应触达数据库
            Assert.True(!database.Commands.Any(c => c.OperationName == "InsertFaceEvent"));
        }

        [TestCase]
        public static void FaceEventRepository_InsertEvents_NullConnectionFactory_FallsBackToSingleRow()
        {
            // 未配置连接串（连接工厂为 null）时，批量方法应降级为逐条 InsertEvent。
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var repository = new FaceEventRepository(database, storage);

            var events = new List<AcsFaceEvent>
            {
                NewEvent(1001),
                NewEvent(1002)
            };
            var results = repository.InsertEvents(events);

            Assert.Equal(2, results.Count);
            Assert.True(results.All(r => r.Success));
            // 降级为逐条：应有 2 次 InsertFaceEvent + 2 次 CheckFaceEventDuplicate 查重
            var inserts = database.Commands.Where(c => c.OperationName == "InsertFaceEvent").ToList();
            Assert.Equal(2, inserts.Count);
        }

        [TestCase]
        public static void FaceEventRepository_InsertEvents_PreQueryFailure_ReturnsRetryableFailures()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var connectionFactoryCalls = 0;
            var repository = new FaceEventRepository(
                database,
                storage,
                new Func<System.Data.SqlClient.SqlConnection>(() =>
                {
                    connectionFactoryCalls++;
                    throw new InvalidOperationException(connectionFactoryCalls == 1
                        ? "prequery connection failure"
                        : "unexpected later connection failure");
                }));

            var results = repository.InsertEvents(new List<AcsFaceEvent>
            {
                NewEvent(1101),
                NewEvent(1102)
            });

            Assert.Equal(2, results.Count);
            Assert.True(results.All(r => r.Status == FaceEventInsertStatus.RetryableFailure));
            Assert.True(results.All(r => r.Code == "RETRYABLE_FAILURE"));
            Assert.True(results.All(r => r.Message.Contains("prequery connection failure")));
            Assert.Equal(1, connectionFactoryCalls);
            Assert.True(!database.Commands.Any(c => c.OperationName == "InsertFaceEvent"));
        }

        // ===== Processor 批量逻辑 =====

        [TestCase]
        public static void AcsFaceEventProcessor_ProcessBatch_RepositoryException_ReturnsStructuredFailures()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var repository = new FaceEventRepository(
                database,
                storage,
                new Func<System.Data.SqlClient.SqlConnection>(() => { throw new InvalidOperationException("transient connection failure"); }));
            var processor = new AcsFaceEventProcessor(new AcsEventParser(), repository);

            var raw = new List<RawAcsAlarmEvent>
            {
                NewRawEvent(2001),
                NewRawEvent(2002)
            };

            var results = processor.ProcessBatch(raw);

            Assert.Equal(2, results.Count);
            Assert.True(results.All(r => !r.Success));
            Assert.True(results.All(r => r.Code == "RETRYABLE_FAILURE"));
        }

        [TestCase]
        public static void AcsFaceEventProcessor_ProcessBatch_UnexpectedRepositoryException_ReturnsStructuredFailures()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var repository = new FaceEventRepository(database, storage);
            var processor = new AcsFaceEventProcessor(
                new AcsEventParser(),
                repository,
                events => { throw new InvalidOperationException("repository escaped"); });

            var raw = new List<RawAcsAlarmEvent>
            {
                NewRawEvent(2101),
                NewRawEvent(2102)
            };

            var results = processor.ProcessBatch(raw);

            Assert.Equal(2, results.Count);
            Assert.True(results.All(r => !r.Success));
            Assert.True(results.All(r => r.Code == "RETRYABLE_FAILURE"));
            Assert.True(results.All(r => r.Message.Contains("repository escaped")));
        }

        [TestCase]
        public static void AcsFaceEventProcessor_ProcessBatch_ParseFailuresSkippedDb()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var processor = new AcsFaceEventProcessor(new AcsEventParser(), new FaceEventRepository(database, storage));

            // 第 1 条：有效；第 2 条：缺 employeeId（parse 失败）；第 3 条：有效
            var raw = new List<RawAcsAlarmEvent>
            {
                NewRawEvent(9001),
                NewRawEventWithoutEmployee(9002),
                NewRawEvent(9003)
            };

            var results = processor.ProcessBatch(raw);

            Assert.Equal(3, results.Count);
            Assert.True(results[0].Success);
            Assert.False(results[1].Success);
            Assert.True(results[2].Success);
            // 只有 2 条有效事件进 DB（降级逐条，各 1 次 insert + 1 次查重）
            var inserts = database.Commands.Where(c => c.OperationName == "InsertFaceEvent").ToList();
            Assert.Equal(2, inserts.Count);
        }

        // ===== Ingestion service 攒批 =====

        [TestCase]
        public static void FaceEventIngestionService_FlushByBatchSize()
        {
            int batchSize = 2;
            var processor = new CountingBatchProcessor();
            var options = new FaceEventLoggingOptions
            {
                QueueCapacity = 20,
                BatchSize = batchSize,
                FlushIntervalMs = 5000   // 拉长，确保由数量阈值触发而非时间
            };
            var service = new FaceEventIngestionService(options, processor);
            var context = new BackgroundTaskContext("batch-size-test", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent(i + 1)).Accepted);
                }

                Assert.True(WaitUntil(() => processor.BatchCount >= 1 && processor.TotalProcessed >= 2, 2000),
                    "首批 2 条应被数量阈值触发 flush");
                Assert.True(processor.BatchCount >= 1);
                Assert.True(processor.TotalProcessed >= 2);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_FlushByInterval()
        {
            var processor = new CountingBatchProcessor();
            var options = new FaceEventLoggingOptions
            {
                QueueCapacity = 20,
                BatchSize = 100,       // 拉大，确保不会由数量阈值触发
                FlushIntervalMs = 50   // 短时间阈值
            };
            var service = new FaceEventIngestionService(options, processor);
            var context = new BackgroundTaskContext("interval-test", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                Assert.True(service.TryEnqueue(NewRawEvent(1)).Accepted);

                Assert.True(WaitUntil(() => processor.TotalProcessed >= 1, 2000),
                    "单条事件应被时间阈值触发 flush");
                Assert.True(processor.TotalProcessed >= 1);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestionService_BatchProcessorFailure_DoesNotKillWorker()
        {
            var processor = new ThrowingBatchProcessor();
            var options = new FaceEventLoggingOptions
            {
                QueueCapacity = 20,
                BatchSize = 1,
                FlushIntervalMs = 50
            };
            var service = new FaceEventIngestionService(options, processor);
            var context = new BackgroundTaskContext("failure-test", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                // 第 1 条会让 ProcessBatch 抛异常
                Assert.True(service.TryEnqueue(NewRawEvent(1)).Accepted);
                Assert.True(WaitUntil(() => processor.ThrowCount >= 1, 2000), "第一批应触发异常");

                // worker 应存活，第 2 条仍能被处理
                processor.ThrowOnNext = false;
                Assert.True(service.TryEnqueue(NewRawEvent(2)).Accepted);
                Assert.True(WaitUntil(() => processor.SuccessCount >= 1, 2000), "异常后 worker 应继续处理");
                Assert.True(processor.SuccessCount >= 1);
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        // ===== 辅助 =====

        private static AcsFaceEvent NewEvent(long eventId)
        {
            return new AcsFaceEvent
            {
                EventId = eventId,
                EmployeeId = "emp" + eventId,
                EventTime = new DateTime(2026, 6, 14, 10, 0, 0),
                DeviceId = 7,
                DeviceName = "Gate",
                DeviceSn = "SN-7"
            };
        }

        private static RawAcsAlarmEvent NewRawEvent(int seq)
        {
            var raw = new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 6, 14, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                DeviceName = "Front Gate",
                DeviceSerialNo = "SN-7",
                Source = AcsAlarmEventSource.Realtime,
                RequestId = "req-" + seq
            };
            raw.Values["employeeId"] = "emp" + seq;
            raw.Values["dwSerialNo"] = (seq * 10).ToString();
            raw.Values["dwMinor"] = "75";
            raw.Values["success"] = "true";
            return raw;
        }

        private static RawAcsAlarmEvent NewRawEventWithoutEmployee(int seq)
        {
            var raw = NewRawEvent(seq);
            raw.Values.Remove("employeeId");
            return raw;
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }
                Thread.Sleep(20);
            }
            return condition();
        }

        private sealed class CountingBatchProcessor : IAcsFaceEventProcessor
        {
            public int BatchCount;
            public int TotalProcessed;

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                TotalProcessed++;
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                BatchCount++;
                TotalProcessed += rawEvents.Count;
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var raw in rawEvents)
                {
                    results.Add(FaceEventBatchItemResult.Ok(0, "INSERTED", "ok"));
                }
                return results;
            }
        }

        private sealed class ThrowingBatchProcessor : IAcsFaceEventProcessor
        {
            public bool ThrowOnNext = true;
            public int ThrowCount;
            public int SuccessCount;

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                if (ThrowOnNext)
                {
                    ThrowCount++;
                    ThrowOnNext = false;
                    throw new InvalidOperationException("batch processor exploded");
                }
                SuccessCount++;
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                if (ThrowOnNext)
                {
                    ThrowCount++;
                    ThrowOnNext = false;
                    throw new InvalidOperationException("batch processor exploded");
                }
                SuccessCount += rawEvents.Count;
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var raw in rawEvents)
                {
                    results.Add(FaceEventBatchItemResult.Ok(0, "INSERTED", "ok"));
                }
                return results;
            }
        }
    }
}
