using System;
using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class PermissionInfo
    {
        public PermissionInfo()
        {
            DoorIndexes = new List<int>();
            Enabled = true;
        }

        public string EmployeeId { get; set; }

        public string PermissionCode { get; set; }

        public IList<int> DoorIndexes { get; private set; }

        public DateTime? ValidFrom { get; set; }

        public DateTime? ValidTo { get; set; }

        public bool Enabled { get; set; }

        public string ScheduleTemplate { get; set; }
    }
}
