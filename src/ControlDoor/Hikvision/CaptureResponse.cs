using System;

namespace ControlDoor.Hikvision
{
    public class CaptureResponse
    {
        public CaptureResponse()
        {
            ImageBytes = new byte[0];
            ContentType = "image/jpeg";
            CapturedAt = DateTime.Now;
        }

        public byte[] ImageBytes { get; set; }

        public string ContentType { get; set; }

        public DateTime CapturedAt { get; set; }
    }
}
