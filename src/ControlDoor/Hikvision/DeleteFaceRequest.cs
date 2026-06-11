namespace ControlDoor.Hikvision
{
    public sealed class DeleteFaceRequest
    {
        public int UserId { get; set; }

        public string EmployeeId { get; set; }

        public string CardNumber { get; set; }

        public string FaceId { get; set; }
    }
}
