using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class SetPermissionRequest
    {
        public SetPermissionRequest()
        {
            Permissions = new List<PermissionInfo>();
        }

        public int UserId { get; set; }

        public IList<PermissionInfo> Permissions { get; private set; }
    }
}
