namespace ControlDoor.Runtime
{
    public sealed class BackgroundTaskRegistration
    {
        public BackgroundTaskRegistration(IBackgroundTask task, int startOrder, int stopOrder, bool isCritical)
        {
            Task = task;
            StartOrder = startOrder;
            StopOrder = stopOrder;
            IsCritical = isCritical;
            Status = new BackgroundTaskStatus(task.Name, isCritical);
        }

        public IBackgroundTask Task { get; }

        public int StartOrder { get; }

        public int StopOrder { get; }

        public bool IsCritical { get; }

        public BackgroundTaskStatus Status { get; }
    }
}
