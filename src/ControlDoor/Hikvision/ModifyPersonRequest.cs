namespace ControlDoor.Hikvision
{
    public sealed class ModifyPersonRequest
    {
        public int UserId { get; set; }

        public PersonInfo Person { get; set; }
    }
}
