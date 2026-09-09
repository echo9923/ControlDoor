using System;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Observability;
using ControlDoor.Runtime;

namespace ControlDoor.FaceEvents
{
    // 历史抓拍清理后台任务（复核 R3）：按保留天数周期清理超期的日期目录。
    // SnapshotRetentionDays=0（默认）表示不启用——保留天数是业务决策，未经确认不删除历史图片；
    // 启用后按 50-200KiB 单图、每天 8000-16000 条的现场口径定期核对磁盘容量（见部署文档）。
    public sealed class SnapshotCleanupBackgroundTask : IBackgroundTask
    {
        public const int DefaultCleanupIntervalMinutes = 60;
        private const int MaxFileDeletionsPerRun = 5000;

        private readonly SnapshotStorage storage;
        private readonly FaceEventLoggingOptions options;
        private readonly ServiceLogger logger;
        private readonly BackgroundTaskStatus status = new BackgroundTaskStatus("SnapshotCleanup", false);
        private CancellationTokenSource cancellation;
        private Task worker;

        public SnapshotCleanupBackgroundTask(SnapshotStorage storage, FaceEventLoggingOptions options, ServiceLogger logger = null)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
            this.options = options ?? new FaceEventLoggingOptions();
            this.logger = logger;
        }

        public string Name => "SnapshotCleanup";

        public bool IsCritical => false;

        public Task StartAsync(BackgroundTaskContext context)
        {
            if (worker != null)
            {
                return Task.CompletedTask;
            }

            if (options.SnapshotRetentionDays <= 0)
            {
                logger?.Info("SnapshotCleanup", "抓拍历史清理未启用（SnapshotRetentionDays=0）；如需自动清理请评估保留天数后在配置中开启。");
                status.MarkStarted();
                return Task.CompletedTask;
            }

            cancellation = new CancellationTokenSource();
            worker = Task.Run(() => RunLoopAsync(cancellation.Token));
            status.MarkStarted();
            return Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            cancellation?.Cancel();
            if (worker != null)
            {
                await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }

            status.MarkStopped();
        }

        public BackgroundTaskStatus GetStatus()
        {
            return status.Clone();
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(5,
                options.SnapshotCleanupIntervalMinutes > 0 ? options.SnapshotCleanupIntervalMinutes : DefaultCleanupIntervalMinutes));
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var cutoff = DateTime.Now.AddDays(-Math.Max(1, options.SnapshotRetentionDays));
                    var result = storage.CleanupExpired(cutoff, MaxFileDeletionsPerRun);
                    if (result.DeletedFiles > 0 || result.FailedFiles > 0)
                    {
                        var fields = new LogFields
                        {
                            OperationName = "SnapshotCleanup"
                        };
                        fields.Extra["deletedFiles"] = result.DeletedFiles.ToString();
                        fields.Extra["deletedBytes"] = result.DeletedBytes.ToString();
                        fields.Extra["failedFiles"] = result.FailedFiles.ToString();
                        fields.Extra["removedDirectories"] = result.RemovedDirectories.ToString();
                        fields.Extra["retentionDays"] = Math.Max(1, options.SnapshotRetentionDays).ToString();
                        logger?.Info("SnapshotCleanup", "历史抓拍清理完成。", fields);
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error("SnapshotCleanup", "历史抓拍清理轮次异常，下一轮重试。", ex);
                }

                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
