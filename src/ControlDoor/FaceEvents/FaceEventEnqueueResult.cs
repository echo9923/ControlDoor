namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventEnqueueResult
    {
        public bool Accepted { get; set; }

        public string Code { get; set; }

        public string Message { get; set; }

        public int QueueDepth { get; set; }

        public int Capacity { get; set; }

        public static FaceEventEnqueueResult AcceptedResult(int queueDepth, int capacity)
        {
            return new FaceEventEnqueueResult
            {
                Accepted = true,
                Code = "OK",
                Message = "accepted",
                QueueDepth = queueDepth,
                Capacity = capacity
            };
        }

        public static FaceEventEnqueueResult Rejected(string code, string message, int queueDepth, int capacity)
        {
            return new FaceEventEnqueueResult
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
