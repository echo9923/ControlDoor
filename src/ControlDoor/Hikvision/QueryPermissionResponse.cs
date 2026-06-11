using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class QueryPermissionResponse
    {
        public QueryPermissionResponse()
        {
            Permissions = new List<PermissionInfo>();
        }

        public IList<PermissionInfo> Permissions { get; private set; }

        public int TotalCount { get; set; }

        public string RawResponse { get; set; }
    }
}
