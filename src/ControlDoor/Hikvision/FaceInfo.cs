namespace ControlDoor.Hikvision
{
    public sealed class FaceInfo
    {
        public FaceInfo()
        {
            ImageFormat = "JPEG";
            ImageBytes = new byte[0];
        }

        public string EmployeeId { get; set; }

        public string CardNumber { get; set; }

        public string FaceId { get; set; }

        public byte[] ImageBytes { get; set; }

        public string ImageBase64 { get; set; }

        public string ImageFormat { get; set; }

        public int QualityScore { get; set; }
    }
}
