namespace ControlDoor.Hikvision
{
    public sealed class AlarmDeploymentStatus
    {
        public bool Known { get; set; }

        public bool IsDeployed { get; set; }

        public byte RawSetupAlarmStatus { get; set; }

        public string RawSummary { get; set; }
    }
}
