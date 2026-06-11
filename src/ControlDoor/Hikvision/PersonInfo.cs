using System;
using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class PersonInfo
    {
        public PersonInfo()
        {
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Enabled = true;
        }

        public string EmployeeId { get; set; }

        public string Name { get; set; }

        public string CardNumber { get; set; }

        public string Department { get; set; }

        public DateTime? ValidFrom { get; set; }

        public DateTime? ValidTo { get; set; }

        public bool Enabled { get; set; }

        public IDictionary<string, string> Metadata { get; private set; }
    }
}
