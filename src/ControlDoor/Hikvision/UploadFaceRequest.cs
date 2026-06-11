namespace ControlDoor.Hikvision
{
    public sealed class UploadFaceRequest
    {
        public UploadFaceRequest()
        {
            MaxImageBytes = 200 * 1024;
        }

        public int UserId { get; set; }

        public FaceInfo Face { get; set; }

        public int MaxImageBytes { get; set; }
    }
}
