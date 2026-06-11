namespace ControlDoor.Hikvision
{
    public sealed class LoginRequest
    {
        public LoginRequest()
        {
            Port = 8000;
            UseTcp = true;
            TimeoutMilliseconds = 30000;
        }

        public string IpAddress { get; set; }

        public int Port { get; set; }

        public string UserName { get; set; }

        public string Password { get; set; }

        public bool UseTcp { get; set; }

        public int TimeoutMilliseconds { get; set; }
    }
}
