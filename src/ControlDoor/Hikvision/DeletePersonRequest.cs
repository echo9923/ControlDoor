namespace ControlDoor.Hikvision
{
    public sealed class DeletePersonRequest
    {
        public int UserId { get; set; }

        public string EmployeeId { get; set; }

        public string CardNumber { get; set; }
    }
}
