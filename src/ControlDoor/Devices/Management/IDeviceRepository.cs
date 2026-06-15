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

        DeviceStoreWriteResult InsertDevice(DeviceRecord record);

        DeviceStoreWriteResult DeleteDevice(int deviceId);
    }
}
