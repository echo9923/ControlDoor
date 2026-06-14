namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// AIOP 报警入队结果（镜像 FaceEvents.FaceEventEnqueueResult）。
    /// </summary>
    public sealed class AiopAlarmEnqueueResult
    {
        public bool Accepted { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public int QueueDepth { get; set; }

        public int Capacity { get; set; }

        public static AiopAlarmEnqueueResult AcceptedResult(int queueDepth, int capacity)
        {
            return new AiopAlarmEnqueueResult
            {
                Accepted = true,
                Code = "OK",
                Message = "accepted",
                QueueDepth = queueDepth,
                Capacity = capacity
            };
        }

        public static AiopAlarmEnqueueResult Rejected(string code, string message, int queueDepth, int capacity)
        {
            return new AiopAlarmEnqueueResult
            {
                Accepted = false,
                Code = code,
                Message = message,
                QueueDepth = queueDepth,
                Capacity = capacity
            };
        }
    }
}
