namespace ControlDoor.Permissions
{
    public sealed class RetryStateMergeResult
    {
        public DeviceOperationRetryState State { get; set; }

        public RetryOperation Operation { get; set; }

        public bool Insert { get; set; }

        public bool ReactivatedTerminal { get; set; }

        public bool ConflictReset { get; set; }

        public bool SameKindUpdate { get; set; }

        public bool AttemptCountReset => Insert || ReactivatedTerminal || ConflictReset;
    }
}
