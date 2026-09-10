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
    // 代码复核 R2：数据库整体故障与单条坏数据分开——环境故障不受单条重试上限约束，
    // 保持持久积压等待恢复；死信提供 --replay-dead-letters 受控回放与数量监控。
    public static class CodeReviewR02DeadLetterGovernanceTests
    {
        [TestCase]
        public static void FaceEventIngestion_EnvironmentalFailureBeyondItemLimit_KeepsRetryingUntilRecovery()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new EnvironmentalProcessor();
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions
                {
                    QueueCapacity = 100,
                    BatchSize = 5,
                    FlushIntervalMs = 20,
                    RetryInitialDelayMs = 10,
                    RetryMaxDelayMs = 10,
                    EnvironmentalRetryMaxDelayMs = 10,
                    MaxItemRetryAttempts = 2
                },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext("r02-env-unbounded", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                Assert.True(service.TryEnqueue(NewRawEvent("r02-env-1")).Accepted);
                Assert.True(service.TryEnqueue(NewRawEvent("r02-env-2")).Accepted);

                // 整轮同构 DATABASE_FAILURE：超过单条上限（2 次）后仍持续重试，不进死信。
                WaitUntil(() => processor.FailureCount > 4, "环境故障重试次数未超过单条上限。");
                var deadLetterDir = Path.Combine(retryDirectory, "dead-letter");
                Assert.False(Directory.Exists(deadLetterDir) && Directory.EnumerateFiles(deadLetterDir, "*.json").Any(), "环境故障不得死信。");
                // 每次调度均落盘：持久积压可在数据库恢复后继续。
                Assert.True(Directory.EnumerateFiles(retryDirectory, "*.json").Count() >= 2, "环境故障重试未持久化。");

                // 数据库恢复：积压事件自动补齐。
                processor.Recovered = true;
                WaitUntil(() => processor.SuccessRequestIds.Contains("r02-env-1") && processor.SuccessRequestIds.Contains("r02-env-2"), "恢复后未自动补齐旧事件。");
                WaitUntil(() => !Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "恢复后重试文件未清理。");
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestion_DatabaseSignaturePoisonAfterRecentSuccess_DeadLetters()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var processor = new EnvironmentalProcessor { Recovered = true };
            // 复核 L1：环境宽限窗 = 普通退避封顶（RetryMaxDelayMs，此处 5 秒）。健康事件成功后 5 秒内
            // 的同构数据库失败不按环境故障处理：毒事件先有界重试（上限 2 次），耗尽后死信。
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions
                {
                    QueueCapacity = 100,
                    BatchSize = 5,
                    FlushIntervalMs = 20,
                    RetryInitialDelayMs = 10,
                    RetryMaxDelayMs = 5000,
                    EnvironmentalRetryMaxDelayMs = 30000,
                    MaxItemRetryAttempts = 2
                },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext("r02-poison", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                // 先处理成功一批（环境健康），再持续投递以 DATABASE_FAILURE 失败的毒事件：
                // 宽限窗内持续有成功说明环境未整体故障，毒事件不得借环境分类无限重试。
                Assert.True(service.TryEnqueue(NewRawEvent("r02-healthy")).Accepted);
                WaitUntil(() => processor.SuccessRequestIds.Contains("r02-healthy"), "健康事件未被处理。");

                processor.PoisonRequestIds.Add("r02-poison");
                Assert.True(service.TryEnqueue(NewRawEvent("r02-poison")).Accepted);

                var deadLetterDir = Path.Combine(retryDirectory, "dead-letter");
                WaitUntil(() => Directory.Exists(deadLetterDir) && Directory.EnumerateFiles(deadLetterDir, "*.json").Any(), "宽限窗内有成功时数据库签名毒事件未按上限死信。");
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void FaceEventIngestion_DatabaseOutageAfterRecentSuccess_KeepsRetryingBeyondLimitAndRecovers()
        {
            RunOutageTransitionTest("DATABASE_FAILURE", "l1-outage-transition");
        }

        [TestCase]
        public static void FaceEventIngestion_RetryableFailureOutageAfterRecentSuccess_KeepsRetryingBeyondLimitAndRecovers()
        {
            // 复核 L2：仓储预查询失败/超时/暂态 SQL 错误返回 RETRYABLE_FAILURE，整轮同签名
            // 也必须进入环境保护，不得按单条 10 次上限死信。
            RunOutageTransitionTest("RETRYABLE_FAILURE", "l2-outage-transition");
        }

        // 复核 L1/L2 验收：先成功一批（宽限窗被刷新）→ 数据库立即整体故障 → 事件先有界重试，
        // 成功停止、宽限窗（普通退避封顶）到期后自动升级环境保护，每条事件的重试次数都超过
        // 单条上限（MaxItemRetryAttempts + 1 次）仍不死信且补偿已持久化；数据库恢复后全部
        // 自动补齐、补偿文件清空。时序约束：有界重试累计跨度（250 + 500 = 750ms）必须大于
        // 宽限窗（500ms），保证第 3 次（上限）尝试发生时环境保护已介入。
        // 复核 M 轮补强：按事件标识分别计数，不用 3 条事件的累计失败次数推断"超过上限"。
        private static void RunOutageTransitionTest(string failureCode, string requestIdPrefix)
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            var processor = new EnvironmentalProcessor { Recovered = true, FailureCode = failureCode };
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions
                {
                    QueueCapacity = 100,
                    BatchSize = 5,
                    FlushIntervalMs = 20,
                    RetryInitialDelayMs = 250,
                    RetryMaxDelayMs = 500,
                    EnvironmentalRetryMaxDelayMs = 500,
                    MaxItemRetryAttempts = 3
                },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext(requestIdPrefix, CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                var requestIds = new[] { requestIdPrefix + "-1", requestIdPrefix + "-2", requestIdPrefix + "-3" };

                // 正常运行：先成功一条，随后数据库整体故障（宽限窗内首败是 L1 的关键过渡点）。
                Assert.True(service.TryEnqueue(NewRawEvent(requestIdPrefix + "-healthy")).Accepted);
                WaitUntil(() => processor.SuccessRequestIds.Contains(requestIdPrefix + "-healthy"), "健康事件未被处理。");
                processor.Recovered = false;
                foreach (var requestId in requestIds)
                {
                    Assert.True(service.TryEnqueue(NewRawEvent(requestId)).Accepted);
                }

                // 每条故障事件都至少失败 MaxItemRetryAttempts + 1 = 4 次：只有环境保护介入才会
                // 让单条重试突破上限；期间不得产生死信，且补偿记录已持久化到重试目录。
                WaitUntil(
                    () => requestIds.All(id => processor.GetFailureCount(id) > 3),
                    "故障事件按事件计数未全部超过单条上限，环境保护未介入。");
                Assert.False(Directory.Exists(deadLetterDirectory) && Directory.EnumerateFiles(deadLetterDirectory, "*.json").Any(),
                    "数据库整体故障过渡期不得把事件按上限死信。");
                Assert.True(Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "故障期间补偿记录未持久化。");

                // 数据库恢复：旧事件全部自动补齐、补偿文件清空，始终不产生死信。
                processor.Recovered = true;
                WaitUntil(() => requestIds.All(id => processor.SuccessRequestIds.Contains(id)), "恢复后未自动补齐故障期间事件。");
                WaitUntil(() => !Directory.EnumerateFiles(retryDirectory, "*.json").Any(), "恢复后补偿文件未清空。");
                Assert.False(Directory.Exists(deadLetterDirectory) && Directory.EnumerateFiles(deadLetterDirectory, "*.json").Any(),
                    "整体故障场景不应产生死信。");
            }
            finally
            {
                service.StopAsync(context).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void DeadLetterReplayTool_MovesDeadLettersBackForReplay()
        {
            var runDirectory = TestWorkspace.Create();
            var retryDirectory = Path.Combine(runDirectory, "acs-retry");
            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");

            // 用真实 spool 产生一份合法死信文件（含事件与失败原因）。
            var writer = new AcsEventRetrySpool(retryDirectory, null);
            var item = NewRawEvent("r02-replay");
            writer.DeadLetter(item, "RETRYABLE_FAILURE", "exhausted");
            Assert.Equal(1, Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count());

            var result = DeadLetterReplayTool.ReplayToRetryDirectory(retryDirectory);
            Assert.Equal(1, result.DeadLetterFilesFound);
            Assert.Equal(1, result.Replayed);
            Assert.Equal(0, result.Failed);
            Assert.False(Directory.EnumerateFiles(deadLetterDirectory, "*.json").Any(), "死信文件应被移走。");
            var moved = Directory.EnumerateFiles(retryDirectory, "*.json").Single();
            Assert.False(Path.GetFileName(moved).StartsWith("dl-", StringComparison.OrdinalIgnoreCase), "移回文件应使用新名称避免配对歧义。");

            // 移回后按普通重试文件可被回放（额外字段被忽略）。
            var reader = new AcsEventRetrySpool(retryDirectory, null);
            var loaded = reader.Load(10);
            Assert.Equal(1, loaded.Count);
            Assert.Equal("r02-replay", loaded[0].RequestId);

            // 幂等：目录已空时再次执行不报错。
            reader.Complete(loaded[0]);
            var second = DeadLetterReplayTool.ReplayToRetryDirectory(retryDirectory);
            Assert.Equal(0, second.DeadLetterFilesFound);
            Assert.Equal(0, second.Failed);
        }

        [TestCase]
        public static void CommandLineOptions_ReplayDeadLetters_ParsesMode()
        {
            var options = ControlDoor.CommandLineOptions.Parse(new[] { "--replay-dead-letters" }, userInteractive: false);
            Assert.Equal(ControlDoor.RunMode.ReplayDeadLetters, options.Mode);

            var validate = ControlDoor.CommandLineOptions.Parse(new[] { "--validate-config" }, userInteractive: false);
            Assert.Equal(ControlDoor.RunMode.ValidateConfig, validate.Mode);
        }

        private static RawAcsAlarmEvent NewRawEvent(string requestId)
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 9, 9, 11, 0, 0),
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

        // 可切换的处理器：未恢复时整批以指定失败码失败（模拟数据库整体故障，默认 DATABASE_FAILURE，
        // 可切换 RETRYABLE_FAILURE 模拟仓储超时/暂态 SQL 错误路径），Recovered 后全部成功；
        // PoisonRequestIds 始终失败（模拟坏数据）。
        private sealed class EnvironmentalProcessor : IAcsFaceEventProcessor
        {
            public readonly HashSet<string> PoisonRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> SuccessRequestIds = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, int> failureCountByRequestId = new Dictionary<string, int>(StringComparer.Ordinal);
            public volatile bool Recovered;
            public string FailureCode = "DATABASE_FAILURE";
            public int FailureCount;

            public int GetFailureCount(string requestId)
            {
                lock (failureCountByRequestId)
                {
                    return failureCountByRequestId.TryGetValue(requestId, out var count) ? count : 0;
                }
            }

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                if (!Recovered || PoisonRequestIds.Contains(rawEvent.RequestId))
                {
                    FailureCount++;
                    lock (failureCountByRequestId)
                    {
                        int count;
                        failureCountByRequestId.TryGetValue(rawEvent.RequestId, out count);
                        failureCountByRequestId[rawEvent.RequestId] = count + 1;
                    }

                    return FaceEventProcessResult.Failed(FailureCode, "database unavailable");
                }

                SuccessRequestIds.Add(rawEvent.RequestId);
                return FaceEventProcessResult.Ok("INSERTED", "ok");
            }

            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> rawEvents)
            {
                var results = new List<FaceEventBatchItemResult>(rawEvents.Count);
                foreach (var rawEvent in rawEvents)
                {
                    var outcome = Process(rawEvent);
                    results.Add(outcome.Success
                        ? FaceEventBatchItemResult.Ok(0, outcome.Code, outcome.Message)
                        : FaceEventBatchItemResult.Failed(0, outcome.Code, outcome.Message));
                }

                return results;
            }
        }
    }
}
