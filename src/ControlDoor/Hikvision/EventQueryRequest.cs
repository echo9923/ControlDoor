using System;
using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class EventQueryRequest
    {
        public EventQueryRequest()
        {
            EventTypes = new List<string>();
            PageIndex = 1;
            PageSize = 100;
        }

        public int UserId { get; set; }

        public DateTime BeginTime { get; set; }

        public DateTime EndTime { get; set; }

        public string EmployeeId { get; set; }

        public IList<string> EventTypes { get; private set; }

        public int PageIndex { get; set; }

        public int PageSize { get; set; }
    }
}
