namespace ControlDoor.Database
{
    public sealed class DatabaseError
    {
        public string OperationName { get; set; } = string.Empty;

        public int? SqlErrorNumber { get; set; }

        public string Message { get; set; } = string.Empty;

        public string ExceptionType { get; set; } = string.Empty;

        public long ElapsedMs { get; set; }

        public bool CanRetry { get; set; }
    }
}
