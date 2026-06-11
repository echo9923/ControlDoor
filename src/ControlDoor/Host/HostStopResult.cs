namespace ControlDoor.Host
{
    public sealed class HostStopResult
    {
        private HostStopResult(bool success, string reason, string message)
        {
            Success = success;
            Reason = reason ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public bool Success { get; }

        public string Reason { get; }

        public string Message { get; }

        public static HostStopResult Succeeded(string reason, string message)
        {
            return new HostStopResult(true, reason, message);
        }

        public static HostStopResult Failed(string reason, string message)
        {
            return new HostStopResult(false, reason, message);
        }
    }
}
