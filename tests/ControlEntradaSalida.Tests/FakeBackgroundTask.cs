using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public sealed class FakeBackgroundTask : IBackgroundTask
    {
        private readonly BackgroundTaskStatus status;
        private readonly IList<string> events;
        private readonly bool failOnStart;
        private readonly TimeSpan stopDelay;

        public FakeBackgroundTask(string name, IList<string> events, bool isCritical = false, bool failOnStart = false, TimeSpan? stopDelay = null)
        {
            Name = name;
            IsCritical = isCritical;
            this.events = events;
            this.failOnStart = failOnStart;
            this.stopDelay = stopDelay ?? TimeSpan.Zero;
            status = new BackgroundTaskStatus(name, isCritical);
        }

        public string Name { get; }

        public bool IsCritical { get; }

        public bool StopSawCancellation { get; private set; }

        public async Task StartAsync(BackgroundTaskContext context)
        {
            events.Add("start:" + Name);
            if (failOnStart)
            {
                throw new InvalidOperationException("start failed");
            }

            status.MarkStarted();
            await Task.CompletedTask;
        }

        public async Task StopAsync(BackgroundTaskContext context)
        {
            StopSawCancellation = context.CancellationToken.IsCancellationRequested;
            events.Add("stop:" + Name);
            if (stopDelay > TimeSpan.Zero)
            {
                await Task.Delay(stopDelay).ConfigureAwait(false);
            }

            status.MarkStopped();
        }

        public BackgroundTaskStatus GetStatus()
        {
            return status.Clone();
        }
    }
}
