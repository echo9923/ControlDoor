namespace ControlDoor.Hikvision
{
    public sealed class GateControlResponse
    {
        public bool Success { get; set; }

        public string Code { get; set; }

        public string Message { get; set; }

        public int GateIndex { get; set; }

        public GateControlCommand Command { get; set; }
    }
}
