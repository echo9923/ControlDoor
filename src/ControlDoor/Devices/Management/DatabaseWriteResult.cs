namespace ControlDoor.Devices.Management
{
    public sealed class DatabaseWriteResult
    {
        public bool Success { get; set; }

        public string Code { get; set; } = "OK";

        public string Message { get; set; } = string.Empty;

        public int? RowsAffected { get; set; }

        public static DatabaseWriteResult Ok(int? rowsAffected = null, string message = "OK")
        {
            return new DatabaseWriteResult
            {
                Success = true,
                Code = "OK",
                Message = message,
                RowsAffected = rowsAffected
            };
        }

        public static DatabaseWriteResult Failed(string code, string message)
        {
            return new DatabaseWriteResult
            {
                Success = false,
                Code = string.IsNullOrWhiteSpace(code) ? "DB_ERROR" : code,
                Message = message ?? string.Empty
            };
        }
    }
}
