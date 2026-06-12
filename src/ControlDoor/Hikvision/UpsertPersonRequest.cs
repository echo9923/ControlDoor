namespace ControlDoor.Hikvision
{
    public sealed class UpsertPersonRequest
    {
        public int UserId { get; set; }

        public PersonInfo Person { get; set; }
    }
}
