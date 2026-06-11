using System;

namespace ControlDoor.Runtime
{
    public sealed class BackgroundTaskStatus
    {
        public BackgroundTaskStatus(string name, bool isCritical)
        {
            Name = name;
            IsCritical = isCritical;
        }

        public string Name { get; }

        public bool IsRunning { get; private set; }

        public bool IsCritical { get; }

        public DateTime? StartedAt { get; private set; }

        public DateTime? StoppedAt { get; private set; }

        public string LastError { get; private set; }

        public DateTime? LastHeartbeatAt { get; private set; }

        public bool StopTimedOut { get; private set; }

        public void Heartbeat()
        {
            LastHeartbeatAt = DateTime.Now;
        }

        public void MarkStarting()
        {
            StopTimedOut = false;
            LastError = null;
        }

        public void MarkStarted()
        {
            IsRunning = true;
            StartedAt = DateTime.Now;
            LastHeartbeatAt = StartedAt;
        }

        public void MarkStopped()
        {
            IsRunning = false;
            StoppedAt = DateTime.Now;
        }

        public void MarkFailed(Exception ex)
        {
            IsRunning = false;
            LastError = ex.GetType().Name + ": " + ex.Message;
            StoppedAt = DateTime.Now;
        }

        public void MarkStopTimedOut()
        {
            IsRunning = false;
            StopTimedOut = true;
            StoppedAt = DateTime.Now;
        }

        public BackgroundTaskStatus Clone()
        {
            var clone = new BackgroundTaskStatus(Name, IsCritical);
            if (IsRunning)
            {
                clone.MarkStarted();
            }

            if (StartedAt.HasValue)
            {
                clone.StartedAt = StartedAt;
            }

            clone.StoppedAt = StoppedAt;
            clone.LastError = LastError;
            clone.LastHeartbeatAt = LastHeartbeatAt;
            clone.StopTimedOut = StopTimedOut;
            return clone;
        }
    }
}
