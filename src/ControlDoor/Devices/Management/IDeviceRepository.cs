using System.Collections.Generic;

namespace ControlDoor.Devices.Management
{
    public interface IDeviceRepository
    {
        IReadOnlyList<DeviceRecord> LoadEnabledDevices();

        IReadOnlyList<DeviceRecord> LoadAllDevices();

        DeviceRecord GetByDeviceId(int deviceId);

        bool ExistsDeviceId(int deviceId);

        bool ExistsIpAddress(string ipAddress);

        DatabaseWriteResult InsertDevice(DeviceRecord record);

        DatabaseWriteResult DeleteDevice(int deviceId);

        DatabaseWriteResult UpdateLastUsedTime(int deviceId);
    }
}
