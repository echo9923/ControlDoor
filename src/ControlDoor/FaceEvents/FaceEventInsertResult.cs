namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventInsertResult
    {
        public FaceEventInsertStatus Status { get; set; }

        public bool Success => Status == FaceEventInsertStatus.Inserted || Status == FaceEventInsertStatus.Duplicate;

        public string Code { get; set; }

        public string Message { get; set; }

        public long EventId { get; set; }

        public string SnapshotPath { get; set; }

        public static FaceEventInsertResult Inserted(long eventId, string snapshotPath)
        {
            return new FaceEventInsertResult
            {
                Status = FaceEventInsertStatus.Inserted,
                Code = "INSERTED",
                Message = "inserted",
                EventId = eventId,
                SnapshotPath = snapshotPath
            };
        }

        public static FaceEventInsertResult Duplicate(long eventId)
        {
            return new FaceEventInsertResult
            {
                Status = FaceEventInsertStatus.Duplicate,
                Code = "DUPLICATE",
                Message = "duplicate event",
                EventId = eventId
            };
        }

        public static FaceEventInsertResult Invalid(long eventId, string message)
        {
            return new FaceEventInsertResult
            {
                Status = FaceEventInsertStatus.Invalid,
                Code = "INVALID",
                Message = message,
                EventId = eventId
            };
        }

        public static FaceEventInsertResult Failure(FaceEventInsertStatus status, long eventId, string code, string message)
        {
            return new FaceEventInsertResult
            {
                Status = status,
                Code = code,
                Message = message,
                EventId = eventId
            };
        }
    }
}
