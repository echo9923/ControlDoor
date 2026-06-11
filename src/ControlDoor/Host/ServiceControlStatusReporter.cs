using System;

namespace ControlDoor.Host
{
    public interface IServiceControlStatusReporter
    {
        void ReportPending(ServiceLifecycleState state, TimeSpan waitHint);
    }

    public sealed class NoopServiceControlStatusReporter : IServiceControlStatusReporter
    {
        public void ReportPending(ServiceLifecycleState state, TimeSpan waitHint)
        {
        }
    }
}
