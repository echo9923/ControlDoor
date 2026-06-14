using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const double WarningThresholdRatio = 0.8;

        private readonly BlockingCollection<RawAcsAlarmEvent> queue;
        private readonly IAcsFaceEventProcessor processor;
        private readonly ServiceLogger logger;
        private readonly int capacity;
        private readonly int batchSize;
        private readonly int flushIntervalMs;
        private CancellationTokenSource cancellationTokenSource;
        private Task worker;
        private bool accepting = true;
        private bool disposed;

        public FaceEventIngestionService(FaceEventLoggingOptions options, IAcsFaceEventProcessor processor = null, ServiceLogger logger = null)
        {
            options = options ?? new FaceEventLoggingOptions();
            capacity = options.QueueCapacity < 1 ? DefaultQueueCapacity : options.QueueCapacity;
            batchSize = options.BatchSize < 1 ? DefaultBatchSize : options.BatchSize;
            flushIntervalMs = options.FlushIntervalMs < 1 ? DefaultFlushIntervalMs : options.FlushIntervalMs;
            queue = new BlockingCollection<RawAcsAlarmEvent>(new ConcurrentQueue<RawAcsAlarmEvent>(), capacity);
            this.processor = processor;
            this.logger = logger;
        }

        public string Name => "FaceEventIngestion";

        public bool IsCritical => false;

        public int Count => queue.Count;

        public int Capacity => capacity;

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
                    logger?.Error("FaceEventIngestion", "ACS event queue is full.", null, new LogFields
                    {
                        DeviceId = alarmEvent.DeviceId > 0 ? (int?)alarmEvent.DeviceId : null,
                        RequestId = alarmEvent.RequestId,
                        Extra =
                        {
                            ["command"] = alarmEvent.Command.ToString(),
                            ["receivedAt"] = alarmEvent.ReceivedAt.ToString("O"),
                            ["capacity"] = capacity.ToString()
                        }
                    });
                    return FaceEventEnqueueResult.Rejected("QUEUE_FULL", "face event queue is full", Count, capacity);
                }

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

            cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context?.CancellationToken ?? CancellationToken.None);
            worker = Task.Run(() => ProcessLoop(cancellationTokenSource.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            accepting = false;
            cancellationTokenSource?.Cancel();
            if (worker != null)
            {
                await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
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
            cancellationTokenSource?.Cancel();
            queue.Dispose();
            cancellationTokenSource?.Dispose();
        }

        private void ProcessLoop(CancellationToken cancellationToken)
        {
            var batch = new List<RawAcsAlarmEvent>(batchSize);
            RawAcsAlarmEvent lastItem = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    RawAcsAlarmEvent item = null;
                    try
                    {
                        var got = queue.TryTake(out item, flushIntervalMs, cancellationToken);
                        lastItem = item;
                        if (got)
                        {
                            batch.Add(item);
                            // 攒满一批立即 flush；未满时继续取，直到超时或取消。
                            if (batch.Count >= batchSize)
                            {
                                FlushAndClear(batch);
                            }
                            continue;
                        }

                        // 超时未取到：若批次非空，按时间阈值 flush。
                        if (batch.Count > 0)
                        {
                            FlushAndClear(batch);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // flush 失败已在 FlushAndClear 内清空批次并记录，这里兜底记录外层异常。
                        logger?.Error("FaceEventIngestion", "ACS event processing failed.", ex, new LogFields
                        {
                            DeviceId = lastItem != null && lastItem.DeviceId > 0 ? (int?)lastItem.DeviceId : null,
                            RequestId = lastItem?.RequestId
                        });
                    }
                }

                // 取消时把残余批次 flush 完，避免丢数据。
                FlushAndClear(batch);
            }
            finally
            {
                // 停止期间尽力 flush 残余，失败不影响退出。
                if (batch.Count > 0)
                {
                    try { FlushBatch(batch); } catch { /* 停止期间吞掉 flush 异常 */ }
                    batch.Clear();
                }
            }
        }

        // flush 并保证批次一定被清空：即使 flush 抛异常，batch 也会清空，避免残留事件被重复处理。
        private void FlushAndClear(List<RawAcsAlarmEvent> batch)
        {
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                FlushBatch(batch);
            }
            catch (Exception ex)
            {
                // 记录整批失败，但不吞掉事件元数据（用批次第一条做日志锚点）。
                var anchor = batch.Count > 0 ? batch[0] : null;
                logger?.Error("FaceEventIngestion", "ACS batch flush failed.", ex, new LogFields
                {
                    DeviceId = anchor != null && anchor.DeviceId > 0 ? (int?)anchor.DeviceId : null,
                    RequestId = anchor?.RequestId
                });
            }
            finally
            {
                batch.Clear();
            }
        }

        private void FlushBatch(List<RawAcsAlarmEvent> batch)
        {
            if (batch == null || batch.Count == 0)
            {
                return;
            }

            // 单条场景走单条 Process，保持原有日志/行为语义。
            if (batch.Count == 1)
            {
                var item = batch[0];
                var watch = Stopwatch.StartNew();
                var result = processor.Process(item);
                watch.Stop();
                LogProcessResult(item, result.Success, result.Code, result.Message, watch.ElapsedMilliseconds);
                return;
            }

            var batchWatch = Stopwatch.StartNew();
            var results = processor.ProcessBatch(batch);
            batchWatch.Stop();
            for (var i = 0; i < batch.Count && i < results.Count; i++)
            {
                var item = batch[i];
                var r = results[i];
                LogProcessResult(item, r.Success, r.Code, r.Message, batchWatch.ElapsedMilliseconds);
            }
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
