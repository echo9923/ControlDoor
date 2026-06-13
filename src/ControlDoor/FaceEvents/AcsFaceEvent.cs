using System;
using System.Collections.Generic;

namespace ControlDoor.FaceEvents
{
    public sealed class AcsFaceEvent
    {
        public AcsFaceEvent()
        {
            PictureBytes = new byte[0];
            RawPayloadFields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public long EventId { get; set; }

        public bool EventIdGenerated { get; set; }

        public string EmployeeId { get; set; }

        public string Nickname { get; set; }

        public DateTime EventTime { get; set; }

        public DateTime RecordDate => EventTime.Date;

        public TimeSpan RecordTime => EventTime.TimeOfDay;

        public int Direction { get; set; } = 1;

        public string DeviceName { get; set; }

        public string DeviceSn { get; set; }

        public int DeviceId { get; set; }

        public string DeviceIp { get; set; }

        public string CardNo { get; set; }

        public int? EventType { get; set; }

        public string AuthResult { get; set; }

        public byte[] PictureBytes { get; set; }

        public string RawPayload { get; set; }

        public AcsAlarmEventSource Source { get; set; }

        public IDictionary<string, object> RawPayloadFields { get; private set; }
    }
}
