namespace ControlDoor.Hikvision
{
    public sealed class CaptureRequest
    {
        public CaptureRequest()
        {
            Channel = 1;
            PictureQuality = 0;
            TimeoutMilliseconds = 30000;
        }

        public int UserId { get; set; }

        public int Channel { get; set; }

        public int PictureQuality { get; set; }

        public int TimeoutMilliseconds { get; set; }
    }
}
