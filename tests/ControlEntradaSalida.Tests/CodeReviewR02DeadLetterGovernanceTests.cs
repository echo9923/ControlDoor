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
            // 环境宽限窗（= 环境退避封顶）取默认 30 秒：健康事件成功后 30 秒内发生的
            // 同构数据库失败不按环境故障处理，毒事件立即死信。
            var service = new FaceEventIngestionService(
                new FaceEventLoggingOptions
                {
                    QueueCapacity = 100,
                    BatchSize = 5,
                    FlushIntervalMs = 20,
                    RetryInitialDelayMs = 10,
                    RetryMaxDelayMs = 10,
                    EnvironmentalRetryMaxDelayMs = 30000,
                    MaxItemRetryAttempts = 2
                },
                processor, null, retryDirectory);
            var context = new BackgroundTaskContext("r02-poison", CancellationToken.None, null);
            service.StartAsync(context).GetAwaiter().GetResult();
            try
            {
                // 先处理成功一批（环境健康），再持续投递以 DATABASE_FAILURE 失败的毒事件：
                // 近期有成功说明环境未整体故障，毒事件不得借环境分类无限重试。
                Assert.True(service.TryEnqueue(NewRawEvent("r02-healthy")).Accepted);
                WaitUntil(() => processor.SuccessRequestIds.Contains("r02-healthy"), "健康事件未被处理。");

                processor.PoisonRequestIds.Add("r02-poison");
                Assert.True(service.TryEnqueue(NewRawEvent("r02-poison")).Accepted);

                var deadLetterDir = Path.Combine(retryDirectory, "dead-letter");
                WaitUntil(() => Directory.Exists(deadLetterDir) && Directory.EnumerateFiles(deadLetterDir, "*.json").Any(), "近期有成功时数据库签名毒事件未死信。");
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

        // 可切换的处理器：默认整批以 DATABASE_FAILURE 失败（模拟数据库整体故障），
        // Recovered 后全部成功；PoisonRequestIds 始终以 DATABASE_FAILURE 失败（模拟坏数据）。
        private sealed class EnvironmentalProcessor : IAcsFaceEventProcessor
        {
            public readonly HashSet<string> PoisonRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> SuccessRequestIds = new HashSet<string>(StringComparer.Ordinal);
            public volatile bool Recovered;
            public int FailureCount;

            public FaceEventProcessResult Process(RawAcsAlarmEvent rawEvent)
            {
                if (!Recovered || PoisonRequestIds.Contains(rawEvent.RequestId))
                {
                    FailureCount++;
                    return FaceEventProcessResult.Failed("DATABASE_FAILURE", "database unavailable");
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
