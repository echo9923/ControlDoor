using System;
using System.Collections.Generic;
using ControlDoor.Host;

namespace ControlEntradaSalida.Tests
{
    public sealed class RecordingStatusReporter : IServiceControlStatusReporter
    {
        public IList<ServiceLifecycleState> States { get; } = new List<ServiceLifecycleState>();

        public void ReportPending(ServiceLifecycleState state, TimeSpan waitHint)
        {
            States.Add(state);
        }
    }
}
