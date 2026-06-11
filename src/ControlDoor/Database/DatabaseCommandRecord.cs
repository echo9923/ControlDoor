namespace ControlDoor.Database
{
    public sealed class DatabaseCommandRecord
    {
        public string OperationName { get; set; } = string.Empty;

        public string CommandText { get; set; } = string.Empty;

        public int CommandTimeoutSeconds { get; set; }

        public long ElapsedMs { get; set; }

        public int? RowsAffected { get; set; }

        public DatabaseError Error { get; set; }
    }
}
