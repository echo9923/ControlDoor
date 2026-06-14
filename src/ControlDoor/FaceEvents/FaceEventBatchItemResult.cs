namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventBatchItemResult
    {
        public long EventId { get; set; }

        public bool Success { get; set; }

        public string Code { get; set; }

        public string Message { get; set; }

        public static FaceEventBatchItemResult Ok(long eventId, string code = "OK", string message = null)
        {
            return new FaceEventBatchItemResult
            {
                EventId = eventId,
                Success = true,
                Code = code,
                Message = message ?? code
            };
        }

        public static FaceEventBatchItemResult Failed(long eventId, string code, string message)
        {
            return new FaceEventBatchItemResult
            {
                EventId = eventId,
                Success = false,
                Code = code,
                Message = message
            };
        }
    }
}
