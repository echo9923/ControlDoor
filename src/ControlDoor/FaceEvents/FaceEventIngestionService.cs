using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventIngestionService : IBackgroundTask, IRawAcsAlarmEventSink, IDisposable
    {
        public const int DefaultQueueCapacity = 2000;
        public const int DefaultBatchSize = 50;
        public const int DefaultFlushIntervalMs = 500;
        // 批次上限与仓储层 IN 查询分块对齐，防止超过 SQL Server 每命令 2100 参数限制。
        public const int MaxBatchSize = 1000;
        private const double WarningThresholdRatio = 0.8;
        private const int MaxItemRetryAttempts = 10;
        private const int InitialRetryDelayMs = 250;
        private const int MaxRetryDelayMs = 5000;
        // 环境故障（整轮同构非重试失败，典型为数据库整体不可用）退避封顶（复核 R2）：
        // 默认 30 秒，不受单条重试上限约束，保持持久积压等待恢复。
        public const int DefaultEnvironmentalRetryMaxDelayMs = 30000;
        // 死信巡检（复核 J3/R2）：补清理死信遗留源文件并输出死信数量，独立于回放节奏且有文件预算。
        public const int DefaultDeadLetterPatrolIntervalMs = 30000;
        private const int DeadLetterPatrolFileBudget = 2000;
        // 溢出兜底（复核 R1）：内存队列满时事件进入有界溢出通道，由专用写盘线程落盘到现有重试目录，
        // 由既有磁盘回放自动恢复；SDK 回调线程仍只做零等待 TryAdd，不做同步磁盘 IO。
        public const int DefaultOverflowQueueCapacity = 500;

        // 测试可注入更小的重试上限；生产由配置 MaxItemRetryAttempts 提供（复核 R2），缺省 10 次。
        internal int ItemRetryLimit { private get; set; } = MaxItemRetryAttempts;

        private readonly BlockingCollection<RawAcsAlarmEvent> queue;
        private readonly BlockingCollection<RawAcsAlarmEvent> overflowQueue;
        private readonly IAcsFaceEventProcessor processor;
        private readonly ServiceLogger logger;
        private readonly int capacity;
        private readonly int batchSize;
        private readonly int flushIntervalMs;
        private readonly int deadLetterPatrolIntervalMs;
        private readonly int retryInitialDelayMs;
        private readonly int retryMaxDelayMs;
        private readonly int environmentalRetryMaxDelayMs;
        private long lastDeadLetterPatrolTicks;
        private long overflowPersistedCount;
        private long overflowDroppedCount;
        private long lastSuccessTicks;
        private CancellationTokenSource cancellationTokenSource;
        private Task worker;
        private Task overflowWriter;
        private bool accepting = true;
        private bool disposed;
        private long acceptedCount;
        private long completedCount;
        private readonly object batchGate = new object();
        private readonly AcsEventRetrySpool spool;
        private List<RawAcsAlarmEvent> activeBatch = new List<RawAcsAlarmEvent>();
        private readonly List<RetryEntry> retryLane = new List<RetryEntry>();
        private bool lastFlushWasRetry;

        private sealed class RetryEntry
        {
            public RawAcsAlarmEvent Event;
            public DateTime NextRetryAt;
            public int Attempts;
            public string LastCode = string.Empty;
            public string LastMessage = string.Empty;
        }

        private sealed class RoundOutcome
        {
            public bool Success;
            public string Code;
            public string Message;
        }

        public Task Completion => worker ?? Task.CompletedTask;

        public FaceEventIngestionService(FaceEventLoggingOptions options, IAcsFaceEventProcessor processor = null, ServiceLogger logger = null, string retryDirectory = null)
        {
            options = options ?? new FaceEventLoggingOptions();
            capacity = options.QueueCapacity < 1 ? DefaultQueueCapacity : options.QueueCapacity;
            var configuredBatchSize = options.BatchSize < 1 ? DefaultBatchSize : options.BatchSize;
            batchSize = Math.Min(configuredBatchSize, MaxBatchSize);
            flushIntervalMs = options.FlushIntervalMs < 1 ? DefaultFlushIntervalMs : options.FlushIntervalMs;
            deadLetterPatrolIntervalMs = Math.Max(1000, options.DeadLetterPatrolIntervalMs > 0 ? options.DeadLetterPatrolIntervalMs : DefaultDeadLetterPatrolIntervalMs);
            retryInitialDelayMs = Math.Max(10, options.RetryInitialDelayMs > 0 ? options.RetryInitialDelayMs : InitialRetryDelayMs);
            retryMaxDelayMs = Math.Max(retryInitialDelayMs, options.RetryMaxDelayMs > 0 ? options.RetryMaxDelayMs : MaxRetryDelayMs);
            environmentalRetryMaxDelayMs = Math.Max(retryMaxDelayMs, options.EnvironmentalRetryMaxDelayMs > 0 ? options.EnvironmentalRetryMaxDelayMs : DefaultEnvironmentalRetryMaxDelayMs);
            if (options.MaxItemRetryAttempts > 0)
            {
                ItemRetryLimit = options.MaxItemRetryAttempts;
            }

            queue = new BlockingCollection<RawAcsAlarmEvent>(new ConcurrentQueue<RawAcsAlarmEvent>(), capacity);
            var overflowCapacity = Math.Max(1, options.OverflowQueueCapacity > 0 ? options.OverflowQueueCapacity : DefaultOverflowQueueCapacity);
            overflowQueue = new BlockingCollection<RawAcsAlarmEvent>(new ConcurrentQueue<RawAcsAlarmEvent>(), overflowCapacity);
            this.processor = processor;
            this.logger = logger;
            spool = new AcsEventRetrySpool(retryDirectory, logger);
        }

        public string Name => "FaceEventIngestion";

        public bool IsCritical => false;

        public int Count => queue.Count;

        public int Capacity => capacity;

        internal int BatchSizeForTest => batchSize;

        internal AcsEventRetrySpool SpoolForTest => spool;

        public FaceEventEnqueueResult TryEnqueue(RawAcsAlarmEvent alarmEvent)
        {
            if (disposed)
            {
                return FaceEventEnqueueResult.Rejected("STOPPED", "face event queue is disposed", 0, capacity);
            }

            if (alarmEvent == null)
            {
                return FaceEventEnqueueResult.Rejected("INVALID_ARGUMENT", "alarmEvent is required", Count, capacity);
            }

            if (!accepting || queue.IsAddingCompleted)
            {
                return FaceEventEnqueueResult.Rejected("STOPPED", "face event queue is stopped", Count, capacity);
            }

            try
            {
                if (!queue.TryAdd(alarmEvent, 0))
                {
                    return HandleQueueFull(alarmEvent);
                }

                Interlocked.Increment(ref acceptedCount);
                var depth = Count;
                if (depth >= capacity * WarningThresholdRatio)
                {
                    logger?.Warn("FaceEventIngestion", "ACS event queue is near capacity.", new LogFields
                    {
                        DeviceId = alarmEvent.DeviceId > 0 ? (int?)alarmEvent.DeviceId : null,
                        RequestId = alarmEvent.RequestId,
                        Extra =
                        {
                            ["queueDepth"] = depth.ToString(),
                            ["capacity"] = capacity.ToString()
                        }
                    });
                }

                logger?.Debug("FaceEventIngestion", "ACS event enqueued.", new LogFields
                {
                    DeviceId = alarmEvent.DeviceId > 0 ? (int?)alarmEvent.DeviceId : null,
                    RequestId = alarmEvent.RequestId,
                    Extra =
                    {
                        ["source"] = alarmEvent.Source.ToString(),
                        ["queueDepth"] = depth.ToString()
                    }
                });
                return FaceEventEnqueueResult.AcceptedResult(depth, capacity);
            }
            catch (ObjectDisposedException)
            {
                return FaceEventEnqueueResult.Rejected("STOPPED", "face event queue is disposed", 0, capacity);
            }
            catch (InvalidOperationException)
            {
                // 与方法入口检查之间的停止竞态：按已停止拒绝，不进入溢出通道。
                return FaceEventEnqueueResult.Rejected("STOPPED", "face event queue is stopped", Count, capacity);
            }
        }

        // 队列满处理（复核 R1）：优先进入有界溢出通道，由专用写盘线程持久化到现有重试目录；
        // 磁盘回放在队列腾出预算后自动恢复。溢出通道也满（内存与磁盘持续写不动的极端场景）
        // 才最终丢弃并计数——这是文档化的最后边界，强杀窗口（R5）另见运维文档。
        private FaceEventEnqueueResult HandleQueueFull(RawAcsAlarmEvent alarmEvent)
        {
            var fields = new LogFields
            {
                DeviceId = alarmEvent.DeviceId > 0 ? (int?)alarmEvent.DeviceId : null,
                RequestId = alarmEvent.RequestId,
                Extra =
                {
                    ["command"] = alarmEvent.Command.ToString(),
                    ["receivedAt"] = alarmEvent.ReceivedAt.ToString("O"),
                    ["capacity"] = capacity.ToString()
                }
            };

            if (!overflowQueue.TryAdd(alarmEvent, 0))
            {
                Interlocked.Increment(ref overflowDroppedCount);
                fields.Extra["overflowDroppedTotal"] = Interlocked.Read(ref overflowDroppedCount).ToString();
                logger?.Error("FaceEventIngestion", "ACS event queue and overflow lane are full; event dropped.", null, fields);
                return FaceEventEnqueueResult.Rejected("OVERFLOW_FULL", "face event queue and overflow lane are full", Count, capacity);
            }

            Interlocked.Increment(ref overflowPersistedCount);
            fields.Extra["overflowQueuedTotal"] = Interlocked.Read(ref overflowPersistedCount).ToString();
            logger?.WarnRepeated("FaceEventIngestion", "ACS 事件队列已满，事件进入溢出持久化通道。", fields);
            return new FaceEventEnqueueResult
            {
                Accepted = true,
                Code = "QUEUE_FULL_PERSISTED",
                Message = "queue full; event persisted via overflow lane",
                QueueDepth = Count,
                Capacity = capacity
            };
        }

        public Task StartAsync(BackgroundTaskContext context)
        {
            if (processor == null)
            {
                return Task.CompletedTask;
            }

            if (worker != null)
            {
                return Task.CompletedTask;
            }

            cancellationTokenSource = new CancellationTokenSource();
            worker = Task.Run(() => ProcessLoop(cancellationTokenSource.Token));
            overflowWriter = Task.Run(() => OverflowWriteLoop(cancellationTokenSource.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            accepting = false;
            try
            {
                queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // Already completed by an earlier stop/dispose path.
            }

            try
            {
                // 溢出通道停止接收；写盘线程在排空通道后退出（复核 R1）。
                overflowQueue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            if (worker != null)
            {
                var timeout = TimeSpan.FromSeconds(5);
                var completed = await Task.WhenAny(worker, Task.Delay(timeout)).ConfigureAwait(false);
                if (!object.ReferenceEquals(completed, worker))
                {
                    var unfinished = Math.Max(0, Interlocked.Read(ref acceptedCount) - Interlocked.Read(ref completedCount));
                    logger?.Warn("FaceEventIngestion", "ACS event ingestion stop timed out before queue drain completed.", new LogFields
                    {
                        RequestId = context?.RequestId,
                        Extra =
                        {
                            ["queueDepth"] = Count.ToString(),
                            ["unfinishedAccepted"] = unfinished.ToString(),
                            ["capacity"] = capacity.ToString(),
                            ["timeoutMs"] = ((int)timeout.TotalMilliseconds).ToString()
                        }
                    });
                    cancellationTokenSource?.Cancel();
                }
            }

            if (overflowWriter != null)
            {
                var overflowCompleted = await Task.WhenAny(overflowWriter, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                if (!object.ReferenceEquals(overflowCompleted, overflowWriter))
                {
                    logger?.Warn("FaceEventIngestion", "ACS 溢出写盘线程停止超时，剩余事件将由 PersistPending 兜底落盘。");
                    cancellationTokenSource?.Cancel();
                }
            }

            PersistPending();
        }

        public BackgroundTaskStatus GetStatus()
        {
            var status = new BackgroundTaskStatus(Name, IsCritical);
            status.MarkStarted();
            return status;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            accepting = false;
            try { queue.CompleteAdding(); } catch { /* best-effort shutdown */ }
            try { overflowQueue.CompleteAdding(); } catch { /* best-effort shutdown */ }
            cancellationTokenSource?.Cancel();
            PersistPending();
            if (worker == null || worker.IsCompleted)
            {
                queue.Dispose();
                overflowQueue.Dispose();
                cancellationTokenSource?.Dispose();
            }
            else
            {
                _ = worker.ContinueWith(_ =>
                {
                    queue.Dispose();
                    overflowQueue.Dispose();
                    cancellationTokenSource?.Dispose();
                }, TaskScheduler.Default);
            }
        }

        private void PersistPending()
        {
            lock (batchGate)
            {
                foreach (var item in activeBatch) spool.Save(item);
                foreach (var entry in retryLane) spool.Save(entry.Event);
                foreach (var item in queue.ToArray()) spool.Save(item);
            }

            // 溢出通道兜底排空（复核 R1）：写盘线程未启动或停止超时的场合，停止/释放路径仍把
            // 已进入溢出通道的事件全部落盘，不让兜底承诺出现空洞。
            while (overflowQueue.TryTake(out var overflowItem, 0))
            {
                spool.Persist(overflowItem);
            }
        }

        // 溢出写盘线程（复核 R1）：独立于事件消费循环，把队列满时进入溢出通道的事件尽快落盘，
        // 使溢出内存占用在有界容量内周转。单个事件落盘失败仅丢弃该事件并计数（磁盘故障的最后边界）。
        private void OverflowWriteLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    RawAcsAlarmEvent item;
                    try
                    {
                        item = overflowQueue.Take(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // 通道已 CompleteAdding 且为空或已释放：正常退出。
                        break;
                    }

                    try
                    {
                        spool.Persist(item);
                    }
                    catch (Exception ex)
                    {
                        logger?.Error("FaceEventIngestion", "ACS 溢出事件落盘失败，事件丢弃。", ex, new LogFields
                        {
                            RequestId = item.RequestId,
                            DeviceId = item.DeviceId > 0 ? (int?)item.DeviceId : null
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error("FaceEventIngestion", "ACS 溢出写盘线程异常退出。", ex);
            }
        }

        // 死信巡检（复核 J3/R2）：周期补清理死信遗留源文件；死信目录非空时输出告警（计数监控），
        // 提示需要人工确认后通过 --replay-dead-letters 回放。巡检自身异常不影响事件处理主循环。
        private void RunDeadLetterPatrolIfDue()
        {
            var now = DateTime.Now;
            var lastTicks = Interlocked.Read(ref lastDeadLetterPatrolTicks);
            if (lastTicks != 0 && now.Ticks - lastTicks < TimeSpan.FromMilliseconds(deadLetterPatrolIntervalMs).Ticks)
            {
                return;
            }

            Interlocked.Exchange(ref lastDeadLetterPatrolTicks, now.Ticks);
            try
            {
                var result = spool.Patrol(DeadLetterPatrolFileBudget);
                if (result.DeadLetterCount > 0)
                {
                    var fields = new LogFields
                    {
                        OperationName = "DeadLetterPatrol"
                    };
                    fields.Extra["deadLetterCount"] = result.DeadLetterCount.ToString();
                    fields.Extra["leftoverCleaned"] = result.LeftoverCleaned.ToString();
                    fields.Extra["leftoverPending"] = result.LeftoverPending.ToString();
                    logger?.WarnRepeated("FaceEventIngestion", "死信目录存在待人工处理事件，确认修复后可用 --replay-dead-letters 回放。", fields);
                }
                else if (result.LeftoverCleaned > 0)
                {
                    var fields = new LogFields
                    {
                        OperationName = "DeadLetterPatrol"
                    };
                    fields.Extra["leftoverCleaned"] = result.LeftoverCleaned.ToString();
                    fields.Extra["leftoverPending"] = result.LeftoverPending.ToString();
                    logger?.Info("FaceEventIngestion", "死信遗留源文件已补清理。", fields);
                }
            }
            catch (Exception ex)
            {
                logger?.Error("FaceEventIngestion", "死信巡检异常，下一周期重试。", ex);
            }
        }

        private void ProcessLoop(CancellationToken cancellationToken)
        {
            var batch = activeBatch;
            var age = Stopwatch.StartNew();
            var loopRetryDelay = retryInitialDelayMs;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        lock (batchGate)
                        {
                            if (batch.Count == 0)
                            {
                                // 磁盘回放与实时取件使用同一内存准入预算（复核 H2）：重试道达到容量后
                                // 不再加载磁盘事件，剩余事件保留在磁盘，由重试/死信释放名额后继续。
                                var loadBudget = Math.Min(batchSize, Math.Max(0, capacity - retryLane.Count));
                                if (loadBudget > 0)
                                {
                                    var restored = spool.Load(loadBudget);
                                    batch.AddRange(restored);
                                    Interlocked.Add(ref acceptedCount, restored.Count);
                                    age.Restart();
                                }
                            }

                            // Dequeue and publish under the same lock so stop cannot miss an accepted event.
                            // 队列有积压时连续取到批次上限，不再逐条等待。
                            // 重试道达到队列容量后暂停取件：队列通过 QUEUE_FULL 形成背压，持续故障下内存保持有界。
                            while (batch.Count < batchSize && retryLane.Count < capacity && queue.TryTake(out var item, 0))
                            {
                                if (batch.Count == 0) age.Restart();
                                batch.Add(item);
                            }
                        }

                        // 停止排空语义：队列完成且批次与重试道都清空后才退出，已接受事件在重试道中仍会被处理。
                        bool laneEmpty;
                        lock (batchGate) laneEmpty = retryLane.Count == 0;
                        if (batch.Count == 0 && queue.IsCompleted && laneEmpty) break;

                        // 死信巡检在批处理锁外周期执行（复核 J3/R2）：不占用回放预算，不阻塞组批。
                        RunDeadLetterPatrolIfDue();

                        bool flushNow;
                        lock (batchGate)
                        {
                            var now = DateTime.Now;
                            flushNow = batch.Count >= batchSize
                                || (batch.Count > 0 && (age.ElapsedMilliseconds >= flushIntervalMs || queue.IsCompleted))
                                || HasDueRetry(now);
                        }

                        if (!flushNow)
                        {
                            int remainingMs;
                            lock (batchGate)
                            {
                                remainingMs = batch.Count == 0
                                    ? flushIntervalMs
                                    : (int)Math.Max(0, flushIntervalMs - age.ElapsedMilliseconds);
                                var untilRetry = MsUntilNextRetry(DateTime.Now);
                                if (untilRetry < remainingMs) remainingMs = untilRetry;
                            }

                            cancellationToken.WaitHandle.WaitOne(Math.Min(20, Math.Max(1, remainingMs)));
                            continue;
                        }

                        FlushRound(batch);
                        loopRetryDelay = retryInitialDelayMs;
                    }
                    catch (Exception ex)
                    {
                        logger?.Error("FaceEventIngestion", "ACS event processing failed; pending events retained.", ex);
                        try { PersistPending(); }
                        catch (Exception persistenceError)
                        {
                            logger?.Error("FaceEventIngestion", "ACS 重试文件写入失败，事件继续保留在内存中。", persistenceError);
                        }
                        cancellationToken.WaitHandle.WaitOne(loopRetryDelay);
                        loopRetryDelay = Math.Min(retryMaxDelayMs, loopRetryDelay * 2);
                    }
                }
            }
            finally
            {
                PersistPending();
            }
        }

        // 一轮处理 = 活动批次 或 到期重试（二选一）。双方都有待处理内容时按来源轮换（复核 H3）：
        // 到期重试不再独占连续轮次，新事件也不会被重试流量反向阻塞；BatchSize=1 也保证双方都有机会。
        // 失败事件进入重试道按退避重试，超限或不可恢复失败进入死信区，不再占用活动批次。
        private void FlushRound(List<RawAcsAlarmEvent> batch)
        {
            List<RetryEntry> dueRetries;
            int batchCount;
            lock (batchGate)
            {
                var now = DateTime.Now;
                var hasDueRetry = false;
                for (var i = 0; i < retryLane.Count; i++)
                {
                    if (retryLane[i].NextRetryAt <= now)
                    {
                        hasDueRetry = true;
                        break;
                    }
                }

                var hasBatch = batch.Count > 0;
                var processRetry = hasDueRetry && hasBatch ? !lastFlushWasRetry : hasDueRetry;
                lastFlushWasRetry = processRetry;

                dueRetries = new List<RetryEntry>(batchSize);
                if (processRetry)
                {
                    // 到期重试独占本轮：不能先让新事件填满批次再分配"剩余"预算。
                    for (var i = 0; i < retryLane.Count; i++)
                    {
                        if (dueRetries.Count >= batchSize) break;
                        if (retryLane[i].NextRetryAt <= now)
                        {
                            dueRetries.Add(retryLane[i]);
                            retryLane.RemoveAt(i);
                            i--;
                        }
                    }

                    batchCount = 0;
                }
                else
                {
                    batchCount = batch.Count;
                }
            }

            if (batchCount == 0 && dueRetries.Count == 0) return;

            var items = new List<RawAcsAlarmEvent>(batchCount + dueRetries.Count);
            if (batchCount > 0)
            {
                lock (batchGate) items.AddRange(batch);
            }

            for (var i = 0; i < dueRetries.Count; i++) items.Add(dueRetries[i].Event);

            RoundOutcome[] outcomes;
            var watch = Stopwatch.StartNew();
            try
            {
                outcomes = ComputeOutcomes(items);
            }
            catch
            {
                // 处理器异常：本轮重试条目放回重试道，活动批次原样保留，由外层退避后重试。
                lock (batchGate)
                {
                    foreach (var entry in dueRetries)
                    {
                        entry.NextRetryAt = DateTime.Now.AddMilliseconds(retryInitialDelayMs);
                        retryLane.Add(entry);
                    }
                }

                throw;
            }

            // 整轮全部以同一非重试错误失败时更像环境故障（如表缺失/数据库不可用），按可重试退避而不是死信整批。
            // 复核 R2：环境故障不受单条重试上限约束，保持持久积压等待恢复；但"近期曾有成功"说明环境并未整体
            // 故障——此时持续以数据库签名失败的单条事件按坏数据处理（死信），防止毒事件借环境分类无限重试。
            var environmentalFailure = IsUniformNonRetryableFailure(outcomes) && !HadRecentSuccess();
            for (var index = items.Count - 1; index >= 0; index--)
            {
                var item = items[index];
                var outcome = outcomes[index];
                RetryEntry entry = null;
                if (index >= batchCount)
                {
                    entry = dueRetries[index - batchCount];
                }

                try
                {
                    ApplyOutcome(batch, batchCount, index, entry, item, outcome, environmentalFailure, watch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    // 单项状态处理意外异常不应丢事件：保留原处等待下一轮。
                    logger?.Error("FaceEventIngestion", "ACS 事件结果处理失败，事件保留待重试。", ex, new LogFields
                    {
                        DeviceId = item.DeviceId > 0 ? (int?)item.DeviceId : null,
                        RequestId = item.RequestId,
                        ErrorCode = outcome.Code
                    });
                    if (entry != null)
                    {
                        lock (batchGate)
                        {
                            entry.NextRetryAt = DateTime.Now.AddMilliseconds(retryInitialDelayMs);
                            if (!retryLane.Contains(entry))
                            {
                                retryLane.Add(entry);
                            }
                        }
                    }
                }
            }
        }

        private RoundOutcome[] ComputeOutcomes(List<RawAcsAlarmEvent> items)
        {
            var results = items.Count == 1 ? null : processor.ProcessBatch(items);
            var single = items.Count == 1 ? processor.Process(items[0]) : null;
            var outcomes = new RoundOutcome[items.Count];
            for (var index = 0; index < items.Count; index++)
            {
                var success = single != null ? single.Success : results != null && index < results.Count && results[index] != null && results[index].Success;
                var code = single != null ? single.Code : results != null && index < results.Count ? results[index]?.Code : null;
                var message = single != null ? single.Message : results != null && index < results.Count ? results[index]?.Message : null;
                outcomes[index] = new RoundOutcome { Success = success, Code = code, Message = message };
            }

            return outcomes;
        }

        private static bool IsUniformNonRetryableFailure(RoundOutcome[] outcomes)
        {
            if (outcomes == null || outcomes.Length == 0) return false;
            string signature = null;
            foreach (var outcome in outcomes)
            {
                if (outcome == null || outcome.Success) return false;
                if (IsPermanentValidationFailure(outcome.Code)) return false;
                if (IsNonRetryableFailure(outcome.Code))
                {
                    var current = outcome.Code + "|" + outcome.Message;
                    if (signature == null) signature = current;
                    else if (!string.Equals(signature, current, StringComparison.Ordinal)) return false;
                }
                else
                {
                    return false;
                }
            }

            return signature != null;
        }

        // roundBatchCount 是本轮活动批次贡献数（重试独占轮为 0）：删除守卫必须用它，
        // 不能用活动批次实时长度——重试轮的活动批次仍持有新事件，重试结果下标误删会丢已接受事件（复核 G1）。
        private void ApplyOutcome(List<RawAcsAlarmEvent> batch, int roundBatchCount, int index, RetryEntry entry, RawAcsAlarmEvent item, RoundOutcome outcome, bool environmentalFailure, long elapsedMs)
        {
            LogProcessResult(item, outcome.Success, outcome.Code, outcome.Message, elapsedMs);
            if (outcome.Success || IsPermanentValidationFailure(outcome.Code))
            {
                if (outcome.Success)
                {
                    Interlocked.Exchange(ref lastSuccessTicks, DateTime.Now.Ticks);
                }

                lock (batchGate)
                {
                    spool.Complete(item);
                    if (index < roundBatchCount) batch.RemoveAt(index);
                    Interlocked.Increment(ref completedCount);
                }

                return;
            }

            var retryable = outcome.Code == "RETRYABLE_FAILURE" || environmentalFailure;
            var attempts = (entry != null ? entry.Attempts : 0) + 1;
            // 环境故障（复核 R2）不受单条重试上限约束：数据库整体故障期间保持持久积压（每次调度均落盘），
            // 按较长退避无限重试，恢复后自动补齐；单条坏数据与普通瞬时失败仍按上限进入死信区。
            if (retryable && (environmentalFailure || attempts < ItemRetryLimit))
            {
                lock (batchGate)
                {
                    ScheduleRetryLocked(entry, item, attempts, outcome.Code, outcome.Message, environmentalFailure);
                    if (index < roundBatchCount) batch.RemoveAt(index);
                }

                return;
            }

            // 不可恢复失败或重试次数耗尽：转死信区持久隔离，支持修复后重放。
            try
            {
                spool.DeadLetter(item, outcome.Code, outcome.Message);
                Interlocked.Increment(ref completedCount);
                logger?.Error("FaceEventIngestion", "ACS 事件转入死信区。", null, new LogFields
                {
                    DeviceId = item.DeviceId > 0 ? (int?)item.DeviceId : null,
                    RequestId = item.RequestId,
                    ErrorCode = outcome.Code,
                    Extra =
                    {
                        ["attempts"] = attempts.ToString(),
                        ["reason"] = outcome.Message
                    }
                });
                lock (batchGate)
                {
                    if (index < roundBatchCount) batch.RemoveAt(index);
                }
            }
            catch (Exception deadLetterError)
            {
                // 死信写入失败时退回重试道，绝不丢弃事件。
                lock (batchGate)
                {
                    ScheduleRetryLocked(entry, item, Math.Max(attempts, ItemRetryLimit - 1), outcome.Code, outcome.Message);
                    if (index < roundBatchCount) batch.RemoveAt(index);
                }

                logger?.Error("FaceEventIngestion", "ACS 事件死信写入失败，事件保留在重试道。", deadLetterError, new LogFields
                {
                    DeviceId = item.DeviceId > 0 ? (int?)item.DeviceId : null,
                    RequestId = item.RequestId,
                    ErrorCode = outcome.Code
                });
            }
        }

        // 调用方持有 batchGate；先回重试道再尽力落盘，保证事件不因 IO 异常丢失。
        private void ScheduleRetryLocked(RetryEntry entry, RawAcsAlarmEvent item, int attempts, string code, string message, bool environmentalFailure = false)
        {
            if (entry == null)
            {
                entry = new RetryEntry { Event = item };
            }

            entry.Attempts = attempts;
            entry.LastCode = code ?? string.Empty;
            entry.LastMessage = message ?? string.Empty;
            entry.NextRetryAt = DateTime.Now.AddMilliseconds(RetryDelayMs(attempts, environmentalFailure));
            if (!retryLane.Contains(entry))
            {
                retryLane.Add(entry);
            }

            try
            {
                spool.Save(item);
            }
            catch (Exception persistenceError)
            {
                logger?.Error("FaceEventIngestion", "ACS 重试文件写入失败，事件保留在重试道。", persistenceError);
            }
        }

        private int RetryDelayMs(int attempts, bool environmentalFailure = false)
        {
            var cap = environmentalFailure ? environmentalRetryMaxDelayMs : retryMaxDelayMs;
            var shift = Math.Max(0, Math.Min(attempts - 1, 20));
            var delay = (long)retryInitialDelayMs << shift;
            return (int)Math.Min(cap, delay);
        }

        private static bool IsNonRetryableFailure(string code)
        {
            return code == "DATABASE_FAILURE" || code == "FAILED";
        }

        // 复核 R2：环境故障宽限窗口内出现过成功即视为"环境未整体故障"。
        // 窗口取环境退避封顶（默认 30 秒）：数据库整体故障期间不会有成功；部分恢复后
        // 持续以数据库签名失败的单条事件按坏数据死信，不再享受无限重试保护。
        private bool HadRecentSuccess()
        {
            var last = Interlocked.Read(ref lastSuccessTicks);
            if (last == 0)
            {
                return false;
            }

            return DateTime.Now.Ticks - last < TimeSpan.FromMilliseconds(environmentalRetryMaxDelayMs).Ticks;
        }

        private bool HasDueRetry(DateTime now)
        {
            lock (batchGate)
            {
                for (var i = 0; i < retryLane.Count; i++)
                {
                    if (retryLane[i].NextRetryAt <= now) return true;
                }

                return false;
            }
        }

        private int MsUntilNextRetry(DateTime now)
        {
            var best = int.MaxValue;
            lock (batchGate)
            {
                for (var i = 0; i < retryLane.Count; i++)
                {
                    var remaining = (int)Math.Ceiling((retryLane[i].NextRetryAt - now).TotalMilliseconds);
                    if (remaining < best) best = remaining;
                }
            }

            return best;
        }

        private static bool IsPermanentValidationFailure(string code)
        {
            return code == "INVALID_ARGUMENT" || code == "DEVICE_UNRESOLVED" || code == "MISSING_PERSON_KEY" || code == "INVALID";
        }

        private void LogProcessResult(RawAcsAlarmEvent item, bool success, string code, string message, long elapsedMs)
        {
            var fields = new LogFields
            {
                DeviceId = item != null && item.DeviceId > 0 ? (int?)item.DeviceId : null,
                RequestId = item?.RequestId,
                ElapsedMs = elapsedMs,
                ErrorCode = code
            };
            if (item != null)
            {
                fields.Extra["source"] = item.Source.ToString();
            }
            if (success)
            {
                logger?.Debug("FaceEventIngestion", "ACS event processed.", fields);
            }
            else
            {
                logger?.Warn("FaceEventIngestion", message, fields);
            }
        }
    }
}
