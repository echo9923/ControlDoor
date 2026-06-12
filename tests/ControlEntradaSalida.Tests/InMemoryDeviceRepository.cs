using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Management;

namespace ControlEntradaSalida.Tests
{
    public sealed class InMemoryDeviceRepository : IDeviceRepository
    {
        private readonly Dictionary<int, DeviceRecord> records = new Dictionary<int, DeviceRecord>();

        public IList<string> Operations { get; } = new List<string>();

        public bool FailWrites { get; set; }

        public void Add(DeviceRecord record)
        {
            records[record.DeviceId] = Clone(record);
        }

        public IReadOnlyList<DeviceRecord> LoadEnabledDevices()
        {
            Operations.Add("LoadEnabledDevices");
            return records.Values.Where(item => item.Enabled).OrderBy(item => item.DeviceId).Select(Clone).ToList();
        }

        public IReadOnlyList<DeviceRecord> LoadAllDevices()
        {
            Operations.Add("LoadAllDevices");
            return records.Values.OrderBy(item => item.DeviceId).Select(Clone).ToList();
        }

        public DeviceRecord GetByDeviceId(int deviceId)
        {
            Operations.Add("GetByDeviceId:" + deviceId);
            DeviceRecord record;
            return records.TryGetValue(deviceId, out record) ? Clone(record) : null;
        }

        public bool ExistsDeviceId(int deviceId)
        {
            Operations.Add("ExistsDeviceId:" + deviceId);
            return records.ContainsKey(deviceId);
        }

        public bool ExistsIpAddress(string ipAddress)
        {
            Operations.Add("ExistsIpAddress:" + ipAddress);
            return records.Values.Any(item => string.Equals(item.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase));
        }

        public DatabaseWriteResult InsertDevice(DeviceRecord record)
        {
            Operations.Add("InsertDevice:" + record.DeviceId);
            if (FailWrites)
            {
                return DatabaseWriteResult.Failed("DB_ERROR", "forced failure");
            }

            records[record.DeviceId] = Clone(record);
            return DatabaseWriteResult.Ok(1);
        }

        public DatabaseWriteResult DeleteDevice(int deviceId)
        {
            Operations.Add("DeleteDevice:" + deviceId);
            if (FailWrites)
            {
                return DatabaseWriteResult.Failed("DB_ERROR", "forced failure");
            }

            records.Remove(deviceId);
            return DatabaseWriteResult.Ok(1);
        }

        public DatabaseWriteResult UpdateLastUsedTime(int deviceId)
        {
            Operations.Add("UpdateLastUsedTime:" + deviceId);
            DeviceRecord record;
            if (records.TryGetValue(deviceId, out record))
            {
                record.LastUsedAt = DateTime.Now;
            }

            return DatabaseWriteResult.Ok(1);
        }

        private static DeviceRecord Clone(DeviceRecord source)
        {
            return new DeviceRecord
            {
                DeviceId = source.DeviceId,
                DeviceName = source.DeviceName,
                Description = source.Description,
                IpAddress = source.IpAddress,
                Port = source.Port,
                Username = source.Username,
                Password = source.Password,
                Enabled = source.Enabled,
                LastUsedAt = source.LastUsedAt
            };
        }
    }
}
