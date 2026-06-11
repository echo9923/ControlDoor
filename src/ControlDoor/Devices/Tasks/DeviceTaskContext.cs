using System.Threading;
using ControlDoor.Devices.Runtime;
using ControlDoor.Observability;

namespace ControlDoor.Devices.Tasks
{
    public sealed class DeviceTaskContext
    {
        public DeviceTaskContext(
            DeviceSdkTask task,
            DeviceRuntimeRegistry registry,
            DeviceRuntimeSnapshot snapshotBeforeExecution,
            RequestContext requestContext,
            ServiceLogger logger,
            CancellationToken cancellationToken)
        {
            Task = task;
            Registry = registry;
            SnapshotBeforeExecution = snapshotBeforeExecution;
            RequestContext = requestContext;
            Logger = logger;
            CancellationToken = cancellationToken;
        }

        public DeviceSdkTask Task { get; }

        public DeviceRuntimeRegistry Registry { get; }

        public DeviceRuntimeSnapshot SnapshotBeforeExecution { get; }

        public RequestContext RequestContext { get; }

        public ServiceLogger Logger { get; }

        public CancellationToken CancellationToken { get; }
    }
}
