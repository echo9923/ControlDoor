namespace ControlDoor.Hikvision
{
    public sealed class UpsertPersonRequest
    {
        public UpsertPersonRequest()
        {
            ProvisioningMode = PersonProvisioningMode.Person;
        }

        public int UserId { get; set; }

        public PersonInfo Person { get; set; }

        public PersonProvisioningMode ProvisioningMode { get; set; }
    }
}
