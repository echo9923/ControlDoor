namespace ControlDoor.Permissions
{
    public sealed class DeviceOperationRetryWriteResult
    {
        public bool Success { get; set; }

        public string Code { get; set; } = "OK";

        public string Message { get; set; } = string.Empty;

        public DeviceOperationRetryIntent Intent { get; set; }

        public static DeviceOperationRetryWriteResult Ok(DeviceOperationRetryIntent intent, string message = "补偿意图已写入。")
        {
            return new DeviceOperationRetryWriteResult
            {
                Success = true,
                Code = "OK",
                Message = message,
                Intent = intent
            };
        }

        public static DeviceOperationRetryWriteResult Failed(DeviceOperationRetryIntent intent, string code, string message)
        {
            return new DeviceOperationRetryWriteResult
            {
                Success = false,
                Code = code ?? "DB_ERROR",
                Message = message ?? string.Empty,
                Intent = intent
            };
        }
    }
}
