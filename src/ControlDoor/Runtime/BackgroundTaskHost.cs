using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Observability;

namespace ControlDoor.Runtime
{
    public sealed class BackgroundTaskHost : IDisposable
    {
        private readonly List<BackgroundTaskRegistration> registrations = new List<BackgroundTaskRegistration>();
        private readonly object gate = new object();
        private readonly ServiceLogger logger;
        private CancellationTokenSource cancellationTokenSource;
        private bool started;
        private bool disposed;

        public BackgroundTaskHost(ServiceLogger logger = null)
        {
            this.logger = logger;
        }

        public IReadOnlyList<BackgroundTaskRegistration> Registrations
        {
            get
            {
                lock (gate)
                {
                    return registrations.ToList().AsReadOnly();
                }
            }
        }

        public void Register(IBackgroundTask task, int startOrder, int stopOrder, bool isCritical = false)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            lock (gate)
            {
                ThrowIfDisposed();
                if (started)
                {
                    throw new InvalidOperationException("后台任务宿主启动后不允许继续注册任务。");
                }

                var registration = new BackgroundTaskRegistration(task, startOrder, stopOrder, isCritical || task.IsCritical);
                registrations.Add(registration);
                logger?.Info("BackgroundTaskHost", "注册后台任务。", new LogFields
                {
                    OperationName = task.Name,
                    Extra =
                    {
                        ["startOrder"] = startOrder.ToString(),
                        ["stopOrder"] = stopOrder.ToString(),
                        ["critical"] = registration.IsCritical.ToString()
                    }
                });
            }
        }

        public async Task<BackgroundTaskHostStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (started)
                {
                    return BackgroundTaskHostStartResult.Succeeded();
                }

                started = true;
                cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            var failures = new List<string>();
            foreach (var registration in Registrations.OrderBy(item => item.StartOrder))
            {
                var stopwatch = Stopwatch.StartNew();
                var context = new BackgroundTaskContext(RequestContext.Background(registration.Task.Name).RequestId, cancellationTokenSource.Token, logger);
                registration.Status.MarkStarting();
                logger?.Info("BackgroundTaskHost", "启动后台任务。", new LogFields { OperationName = registration.Task.Name, RequestId = context.RequestId });

                try
                {
                    await registration.Task.StartAsync(context).ConfigureAwait(false);
                    registration.Status.MarkStarted();
                    logger?.Info("BackgroundTaskHost", "后台任务启动成功。", new LogFields
                    {
                        OperationName = registration.Task.Name,
                        ElapsedMs = stopwatch.ElapsedMilliseconds
                    });
                }
                catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
                {
                    registration.Status.MarkStopped();
                    logger?.Warn("BackgroundTaskHost", "后台任务启动被取消。", new LogFields { OperationName = registration.Task.Name });
                }
                catch (Exception ex)
                {
                    registration.Status.MarkFailed(ex);
                    var failure = registration.Task.Name + ": " + ex.Message;
                    failures.Add(failure);
                    logger?.Error("BackgroundTaskHost", "后台任务启动失败。", ex, new LogFields { OperationName = registration.Task.Name });
                    if (registration.IsCritical)
                    {
                        return BackgroundTaskHostStartResult.Failed(failures);
                    }
                }
            }

            return failures.Count == 0
                ? BackgroundTaskHostStartResult.Succeeded()
                : BackgroundTaskHostStartResult.Partial(failures);
        }

        public async Task StopAsync(TimeSpan? perTaskTimeout = null)
        {
            List<BackgroundTaskRegistration> snapshot;
            CancellationTokenSource source;
            lock (gate)
            {
                if (!started)
                {
                    return;
                }

                snapshot = registrations.OrderBy(item => item.StopOrder).ToList();
                source = cancellationTokenSource;
                source.Cancel();
            }

            var timeout = perTaskTimeout ?? TimeSpan.FromMilliseconds(10000);
            foreach (var registration in snapshot)
            {
                var stopwatch = Stopwatch.StartNew();
                logger?.Info("BackgroundTaskHost", "停止后台任务。", new LogFields { OperationName = registration.Task.Name });
                try
                {
                    var context = new BackgroundTaskContext(RequestContext.Background(registration.Task.Name).RequestId, source.Token, logger);
                    var stopTask = registration.Task.StopAsync(context);
                    var completed = await Task.WhenAny(stopTask, Task.Delay(timeout)).ConfigureAwait(false);
                    if (completed == stopTask)
                    {
                        await stopTask.ConfigureAwait(false);
                        registration.Status.MarkStopped();
                        logger?.Info("BackgroundTaskHost", "后台任务停止成功。", new LogFields
                        {
                            OperationName = registration.Task.Name,
                            ElapsedMs = stopwatch.ElapsedMilliseconds
                        });
                    }
                    else
                    {
                        registration.Status.MarkStopTimedOut();
                        logger?.Warn("BackgroundTaskHost", "后台任务停止超时。", new LogFields
                        {
                            OperationName = registration.Task.Name,
                            ElapsedMs = stopwatch.ElapsedMilliseconds
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    registration.Status.MarkStopped();
                }
                catch (Exception ex)
                {
                    registration.Status.MarkFailed(ex);
                    logger?.Error("BackgroundTaskHost", "后台任务停止异常。", ex, new LogFields { OperationName = registration.Task.Name });
                }
            }

            lock (gate)
            {
                started = false;
                source.Dispose();
                cancellationTokenSource = null;
            }
        }

        public IReadOnlyList<BackgroundTaskStatus> GetStatuses()
        {
            return Registrations.Select(item => item.Status.Clone()).ToList().AsReadOnly();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (started)
            {
                StopAsync(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
            }

            cancellationTokenSource?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BackgroundTaskHost));
            }
        }
    }
}
