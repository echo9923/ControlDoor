namespace ControlDoor.FaceEvents
{
    public sealed class SnapshotSaveResult
    {
        public bool Saved { get; set; }

        public string SnapshotPath { get; set; }

        public string ErrorCode { get; set; }

        public string ErrorMessage { get; set; }

        public static SnapshotSaveResult None(string code = "NO_PICTURE", string message = "picture bytes are empty")
        {
            return new SnapshotSaveResult
            {
                Saved = false,
                ErrorCode = code,
                ErrorMessage = message
            };
        }

        public static SnapshotSaveResult SavedResult(string path)
        {
            return new SnapshotSaveResult
            {
                Saved = true,
                SnapshotPath = path,
                ErrorCode = "OK",
                ErrorMessage = "saved"
            };
        }

        public static SnapshotSaveResult Failed(string code, string message)
        {
            return new SnapshotSaveResult
            {
                Saved = false,
                ErrorCode = code,
                ErrorMessage = message
            };
        }
    }
}
