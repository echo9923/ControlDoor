namespace ControlDoor.Hikvision
{
    public sealed class AlarmSetupRequest
    {
        public AlarmSetupRequest()
        {
            Level = 1;
            AlarmInfoType = 1;
            DeployType = 0;
        }

        public int UserId { get; set; }

        public int Level { get; set; }

        public int AlarmInfoType { get; set; }

        public int DeployType { get; set; }

        /// <summary>
        /// Kept only for source compatibility. The Hikvision SDK alarm callback is process-wide
        /// and is registered once by <see cref="HikvisionSdkWrapper"/>; production alarm delivery
        /// uses <see cref="IHikvisionGateway.OnAlarmEvent"/>.
        /// </summary>
        public AlarmCallbackDelegate Callback { get; set; }
    }
}
