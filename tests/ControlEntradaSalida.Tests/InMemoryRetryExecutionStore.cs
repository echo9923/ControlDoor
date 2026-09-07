using System;
using ControlDoor.Devices.Tasks;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    internal sealed class InMemoryRetryExecutionStore : IDeviceOperationRetryExecutionStore
    {
        private readonly object gate = new object();
        private DeviceOperationRetryState state;
        private long nextId;

        public bool FailWrites { get; set; }

        public int WriteCount { get; private set; }

        public DeviceOperationRetryState Snapshot
        {
            get { lock (gate) { return state?.Clone(); } }
        }

        public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
        {
            lock (gate)
            {
                WriteCount++;
                if (FailWrites)
                {
                    return DeviceOperationRetryWriteResult.Failed(intent, "DB_ERROR", "Test database unavailable.");
                }
                var isNew = state == null;
                state = new RetryStateMerger().Merge(state, intent, DateTime.Now, true).State;
                if (isNew)
                {
                    state.Id = ++nextId;
                }
                return DeviceOperationRetryWriteResult.Ok(intent);
            }
        }

        public DeviceOperationRetryState LoadIntent(DeviceOperationRetryIntent intent)
        {
            lock (gate)
            {
                return state?.IntentVersion == intent.IntentVersion ? state.Clone() : null;
            }
        }

        public bool IsCurrent(DeviceOperationRetryState candidate)
        {
            lock (gate)
            {
                return candidate != null && state?.Id == candidate.Id && state.IntentVersion == candidate.IntentVersion;
            }
        }

        public void CompleteOnlineOperation(DeviceOperationRetryState candidate, RetryOperation operation, DeviceTaskResult result)
        {
            lock (gate)
            {
                if (!IsCurrent(candidate) || !result.Success)
                {
                    return;
                }
                switch (operation)
                {
                    case RetryOperation.Person: state.PersonPending = false; break;
                    case RetryOperation.Face: state.FacePending = false; break;
                    case RetryOperation.Permission: state.PermissionPending = false; break;
                    case RetryOperation.DeleteFace: state.DeleteFacePending = false; break;
                    case RetryOperation.DeletePerson: state = null; return;
                }
                if (!state.HasPending)
                {
                    state = null;
                }
            }
        }
    }
}
