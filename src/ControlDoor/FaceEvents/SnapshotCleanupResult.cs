namespace ControlDoor.FaceEvents
{
    public sealed class SnapshotCleanupResult
    {
        public int DeletedFiles { get; set; }

        public long DeletedBytes { get; set; }

        public int FailedFiles { get; set; }

        public int RemovedDirectories { get; set; }
    }
}
