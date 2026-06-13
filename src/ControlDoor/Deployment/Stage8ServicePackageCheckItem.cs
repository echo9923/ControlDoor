namespace ControlDoor.Deployment
{
    public sealed class Stage8ServicePackageCheckItem
    {
        public Stage8ServicePackageCheckItem(string name, bool success, string message)
        {
            Name = name;
            Success = success;
            Message = message;
        }

        public string Name { get; }

        public bool Success { get; }

        public string Message { get; }
    }
}
