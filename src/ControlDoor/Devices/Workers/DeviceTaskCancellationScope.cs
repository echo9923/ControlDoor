using System;
using System.Threading;
using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskCancellationScope : IDisposable
    {
        private readonly CancellationTokenSource timeoutSource;
        private readonly CancellationTokenSource linkedSource;

        private DeviceTaskCancellationScope(CancellationTokenSource timeoutSource, CancellationTokenSource linkedSource)
        {
            this.timeoutSource = timeoutSource;
            this.linkedSource = linkedSource;
        }

        public CancellationToken Token => linkedSource.Token;

        public static DeviceTaskCancellationScope Create(DeviceSdkTask task, CancellationToken workerCancellationToken, DateTime startedAt)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            CancellationTokenSource timeoutSource = null;
            if (task.DeadlineAt.HasValue)
            {
                var remaining = task.DeadlineAt.Value - startedAt;
                timeoutSource = new CancellationTokenSource();
                if (remaining <= TimeSpan.Zero)
                {
                    timeoutSource.Cancel();
                }
                else
                {
                    timeoutSource.CancelAfter(remaining);
                }
            }

            var linkedSource = timeoutSource == null
                ? CancellationTokenSource.CreateLinkedTokenSource(workerCancellationToken, task.CallerCancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(workerCancellationToken, task.CallerCancellationToken, timeoutSource.Token);

            return new DeviceTaskCancellationScope(timeoutSource, linkedSource);
        }

        public void Dispose()
        {
            linkedSource.Dispose();
            timeoutSource?.Dispose();
        }
    }
}
