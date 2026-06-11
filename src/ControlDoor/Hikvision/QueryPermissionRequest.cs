namespace ControlDoor.Hikvision
{
    public sealed class QueryPermissionRequest
    {
        public QueryPermissionRequest()
        {
            PageIndex = 1;
            PageSize = 50;
        }

        public int UserId { get; set; }

        public string EmployeeId { get; set; }

        public string PermissionCode { get; set; }

        public int PageIndex { get; set; }

        public int PageSize { get; set; }
    }
}
