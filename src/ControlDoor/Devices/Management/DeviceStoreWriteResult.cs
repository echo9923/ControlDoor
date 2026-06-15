namespace ControlDoor.Devices.Management
{
    public sealed class DeviceStoreWriteResult
    {
        public bool Success { get; set; }

        public string Code { get; set; } = "OK";

        public string Message { get; set; } = string.Empty;

        public int? RowsAffected { get; set; }

        public static DeviceStoreWriteResult Ok(int? rowsAffected = null, string message = "OK")
        {
            return new DeviceStoreWriteResult
            {
                Success = true,
                Code = "OK",
                Message = message,
                RowsAffected = rowsAffected
            };
        }

        public static DeviceStoreWriteResult Failed(string code, string message)
        {
            return new DeviceStoreWriteResult
            {
                Success = false,
                Code = string.IsNullOrWhiteSpace(code) ? "WRITE_FAILED" : code,
                Message = message ?? string.Empty
            };
        }
    }
}
