using System;

namespace ControlDoor.Hikvision
{
    internal sealed class AcsWorkStatus
    {
        public byte[] SetupAlarmStatus { get; set; } = Array.Empty<byte>();
    }
}
