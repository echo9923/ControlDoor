namespace ControlDoor.Permissions
{
    public interface IUserSyncStatusWriter
    {
        void MarkPermissionSynced(string employeeId, int permissionLevel);

        void MarkPersonSynced(string employeeId);

        void MarkPersonDeleted(string employeeId);
    }
}
