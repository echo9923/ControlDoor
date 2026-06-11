using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class IsapiRequest
    {
        public IsapiRequest()
        {
            Method = IsapiMethod.Get;
            ContentType = "application/json";
            TimeoutMilliseconds = 30000;
            Headers = new Dictionary<string, string>();
        }

        public int UserId { get; set; }

        public string BaseAddress { get; set; }

        public string UserName { get; set; }

        public string Password { get; set; }

        public IsapiMethod Method { get; set; }

        public string Path { get; set; }

        public string Body { get; set; }

        public string ContentType { get; set; }

        public int TimeoutMilliseconds { get; set; }

        public IDictionary<string, string> Headers { get; private set; }
    }
}
