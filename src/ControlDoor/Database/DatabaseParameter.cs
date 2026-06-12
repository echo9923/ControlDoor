namespace ControlDoor.Database
{
    public sealed class DatabaseParameter
    {
        public DatabaseParameter()
        {
        }

        public DatabaseParameter(string name, object value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; set; } = string.Empty;

        public object Value { get; set; }
    }
}
