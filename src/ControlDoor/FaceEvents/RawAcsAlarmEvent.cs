using System;
using System.Collections.Generic;

namespace ControlDoor.FaceEvents
{
    public sealed class RawAcsAlarmEvent
    {
        public RawAcsAlarmEvent()
        {
            AlarmInfoBytes = new byte[0];
            PictureBytes = new byte[0];
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public DateTime ReceivedAt { get; set; }

        public int Command { get; set; }

        public int DeviceId { get; set; }

        public string DeviceIp { get; set; }

        public string DeviceName { get; set; }

        public string DeviceSerialNo { get; set; }

        public int AlarmHandle { get; set; }

        public int SdkUserId { get; set; }

        public byte[] AlarmInfoBytes { get; set; }

        public byte[] PictureBytes { get; set; }

        public int? CurrentEventFlag { get; set; }

        public AcsAlarmEventSource Source { get; set; }

        public string RawSummary { get; set; }

        public string RequestId { get; set; }

        public IDictionary<string, string> Values { get; private set; }
    }
}
