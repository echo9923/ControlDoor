using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class EventQueryResponse
    {
        public EventQueryResponse()
        {
            Records = new List<EventRecord>();
        }

        public IList<EventRecord> Records { get; private set; }

        public int TotalCount { get; set; }

        public bool HasMore { get; set; }

        public string RawResponse { get; set; }
    }
}
