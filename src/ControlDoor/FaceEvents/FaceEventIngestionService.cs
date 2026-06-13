using System;
using System.Collections.Concurrent;
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
        private const double WarningThresholdRatio = 0.8;

        private readonly BlockingCollection<RawAcsAlarmEvent> queue;
        private readonly IAcsFaceEventProcessor processor;
        private readonly ServiceLogger logger;
        private readonly int capacity;
        private CancellationTokenSource cancellationTokenSource;
        private Task worker;
        private bool accepting = true;
        private bool disposed;

        public FaceEventIngestionService(FaceEventLoggingOptions options, IAcsFaceEventProcessor processor = null, ServiceLogger logger = null)
        {
            options = options ?? new FaceEventLoggingOptions();
            capacity = options.QueueCapacity < 1 ? DefaultQueueCapacity : options.QueueCapacity;
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
            while (!cancellationToken.IsCancellationRequested)
            {
                RawAcsAlarmEvent item = null;
                try
                {
                    if (!queue.TryTake(out item, 250, cancellationToken))
                    {
                        continue;
                    }

                    var watch = Stopwatch.StartNew();
                    var result = processor.Process(item);
                    watch.Stop();
                    var fields = new LogFields
                    {
                        DeviceId = item.DeviceId > 0 ? (int?)item.DeviceId : null,
                        RequestId = item.RequestId,
                        ElapsedMs = watch.ElapsedMilliseconds,
                        ErrorCode = result.Code
                    };
                    fields.Extra["source"] = item.Source.ToString();
                    if (result.Success)
                    {
                        logger?.Debug("FaceEventIngestion", "ACS event processed.", fields);
                    }
                    else
                    {
                        logger?.Warn("FaceEventIngestion", result.Message, fields);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger?.Error("FaceEventIngestion", "ACS event processing failed.", ex, new LogFields
                    {
                        DeviceId = item != null && item.DeviceId > 0 ? (int?)item.DeviceId : null,
                        RequestId = item?.RequestId
                    });
                }
            }
        }
    }
}
