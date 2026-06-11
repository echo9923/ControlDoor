using System;
using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class AlarmEventData
    {
        public AlarmEventData()
        {
            RawPayload = new byte[0];
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public int AlarmHandle { get; set; }

        public int UserId { get; set; }

        public int Command { get; set; }

        public string EventType { get; set; }

        public string DeviceIpAddress { get; set; }

        public string DeviceSerialNumber { get; set; }

        public string EmployeeId { get; set; }

        public string CardNumber { get; set; }

        public int DoorIndex { get; set; }

        public string Direction { get; set; }

        public bool Success { get; set; }

        public DateTime? EventTime { get; set; }

        public byte[] RawPayload { get; set; }

        public string RawPayloadSummary { get; set; }

        public IDictionary<string, string> Values { get; private set; }
    }
}
