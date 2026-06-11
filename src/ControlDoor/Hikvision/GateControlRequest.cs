namespace ControlDoor.Hikvision
{
    public sealed class GateControlRequest
    {
        public GateControlRequest()
        {
            TimeoutMilliseconds = 10000;
        }

        public int UserId { get; set; }

        public int GateIndex { get; set; }

        public GateControlCommand Command { get; set; }

        public int TimeoutMilliseconds { get; set; }
    }
}
