namespace ControlDoor.FaceEvents
{
    public sealed class FaceEventProcessResult
    {
        public bool Success { get; set; }

        public string Code { get; set; }

        public string Message { get; set; }

        public static FaceEventProcessResult Ok(string code = "OK", string message = null)
        {
            return new FaceEventProcessResult
            {
                Success = true,
                Code = code,
                Message = message ?? code
            };
        }

        public static FaceEventProcessResult Failed(string code, string message)
        {
            return new FaceEventProcessResult
            {
                Success = false,
                Code = code,
                Message = message
            };
        }
    }
}
