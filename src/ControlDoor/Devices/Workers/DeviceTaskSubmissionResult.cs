using ControlDoor.Devices.Tasks;

namespace ControlDoor.Devices.Workers
{
    public sealed class DeviceTaskSubmissionResult
    {
        private DeviceTaskSubmissionResult(bool accepted, int? workerIndex, long? sequence, DeviceTaskResult immediateResult, DeviceSdkTask task)
        {
            Accepted = accepted;
            WorkerIndex = workerIndex;
            Sequence = sequence;
            ImmediateResult = immediateResult;
            Task = task;
        }

        public bool Accepted { get; }

        public int? WorkerIndex { get; }

        public long? Sequence { get; }

        public DeviceTaskResult ImmediateResult { get; }

        public DeviceSdkTask Task { get; }

        public static DeviceTaskSubmissionResult AcceptedResult(DeviceSdkTask task, int workerIndex, long sequence, DeviceTaskResult immediateResult = null)
        {
            return new DeviceTaskSubmissionResult(true, workerIndex, sequence, immediateResult, task);
        }

        public static DeviceTaskSubmissionResult Rejected(DeviceSdkTask task, DeviceTaskResult result)
        {
            return new DeviceTaskSubmissionResult(false, null, null, result, task);
        }
    }
}
