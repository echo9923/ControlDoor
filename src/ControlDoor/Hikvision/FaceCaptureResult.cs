namespace ControlDoor.Hikvision
{
    public sealed class FaceCaptureResult : CaptureResponse
    {
        public string EmployeeId { get; set; }

        public int QualityScore { get; set; }

        public bool FaceDetected { get; set; }
    }
}
