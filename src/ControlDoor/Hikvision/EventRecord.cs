using System;

namespace ControlDoor.Hikvision
{
    public sealed class EventRecord
    {
        public string SerialNumber { get; set; }

        public string EventType { get; set; }

        public string EmployeeId { get; set; }

        public string CardNumber { get; set; }

        public DateTime EventTime { get; set; }

        public string DeviceIpAddress { get; set; }

        public int DoorIndex { get; set; }

        public string Direction { get; set; }

        public bool Success { get; set; }

        public string RawPayload { get; set; }
    }
}
