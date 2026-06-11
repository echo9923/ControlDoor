using System.Threading.Tasks;

namespace ControlDoor.Runtime
{
    public sealed class NoopBackgroundTask : IBackgroundTask
    {
        private readonly BackgroundTaskStatus status;

        public NoopBackgroundTask(string name, bool isCritical = false)
        {
            Name = name;
            IsCritical = isCritical;
            status = new BackgroundTaskStatus(name, isCritical);
        }

        public string Name { get; }

        public bool IsCritical { get; }

        public Task StartAsync(BackgroundTaskContext context)
        {
            status.MarkStarted();
            return Task.CompletedTask;
        }

        public Task StopAsync(BackgroundTaskContext context)
        {
            status.MarkStopped();
            return Task.CompletedTask;
        }

        public BackgroundTaskStatus GetStatus()
        {
            return status.Clone();
        }
    }
}
