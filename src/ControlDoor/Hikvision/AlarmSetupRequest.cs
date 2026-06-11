namespace ControlDoor.Hikvision
{
    public sealed class AlarmSetupRequest
    {
        public AlarmSetupRequest()
        {
            Level = 1;
            AlarmInfoType = 1;
        }

        public int UserId { get; set; }

        public int Level { get; set; }

        public int AlarmInfoType { get; set; }

        public AlarmCallbackDelegate Callback { get; set; }
    }
}
