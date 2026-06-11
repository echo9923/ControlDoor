using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class IsapiResponse
    {
        public IsapiResponse()
        {
            Headers = new Dictionary<string, string>();
        }

        public int StatusCode { get; set; }

        public string Body { get; set; }

        public string ContentType { get; set; }

        public IDictionary<string, string> Headers { get; private set; }

        public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode <= 299;
    }
}
