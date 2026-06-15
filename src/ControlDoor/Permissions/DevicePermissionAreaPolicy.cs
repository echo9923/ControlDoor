namespace ControlDoor.Permissions
{
    public static class DevicePermissionAreaPolicy
    {
        public static DevicePermissionArea ResolveArea(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return DevicePermissionArea.Other;
            }

            var normalized = description.ToLowerInvariant();
            if (normalized.Contains("生产"))
            {
                return DevicePermissionArea.Production;
            }

            if (normalized.Contains("办公"))
            {
                return DevicePermissionArea.Office;
            }

            return DevicePermissionArea.Other;
        }

        public static bool ShouldEnable(DevicePermissionArea area, int permissionLevel)
        {
            switch (permissionLevel)
            {
                case 0:
                    return false;
                case 1:
                    return area == DevicePermissionArea.Office;
                case 2:
                    return area == DevicePermissionArea.Office ||
                        area == DevicePermissionArea.Production ||
                        area == DevicePermissionArea.Other;
                default:
                    return false;
            }
        }

        public static bool ShouldEnable(string description, int permissionLevel)
        {
            return ShouldEnable(ResolveArea(description), permissionLevel);
        }
    }
}
