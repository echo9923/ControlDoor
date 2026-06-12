namespace ControlDoor.Permissions
{
    public sealed class NullUserSyncStatusWriter : IUserSyncStatusWriter
    {
        public void MarkPermissionSynced(string employeeId, int permissionLevel)
        {
        }

        public void MarkPersonSynced(string employeeId)
        {
        }

        public void MarkPersonDeleted(string employeeId)
        {
        }
    }
}
