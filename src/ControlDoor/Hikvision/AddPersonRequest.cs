namespace ControlDoor.Hikvision
{
    public sealed class AddPersonRequest
    {
        public int UserId { get; set; }

        public PersonInfo Person { get; set; }
    }
}
