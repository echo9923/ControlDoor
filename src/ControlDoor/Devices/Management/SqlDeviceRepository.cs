using System;
using System.Collections.Generic;
using ControlDoor.Database;

namespace ControlDoor.Devices.Management
{
    public sealed class SqlDeviceRepository : IDeviceRepository
    {
        private const string SelectFields = @"
SELECT
    device_id,
    device_name,
    description,
    ip_address,
    port,
    username,
    [password],
    status,
    last_used_time
FROM dbo.devices";

        private readonly IDatabaseClient database;

        public SqlDeviceRepository(IDatabaseClient database)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public IReadOnlyList<DeviceRecord> LoadEnabledDevices()
        {
            return QueryDevices("LoadEnabledDevices", SelectFields + @"
WHERE status = 1
ORDER BY device_id");
        }

        public IReadOnlyList<DeviceRecord> LoadAllDevices()
        {
            return QueryDevices("LoadAllDevices", SelectFields + @"
ORDER BY device_id");
        }

        public DeviceRecord GetByDeviceId(int deviceId)
        {
            var rows = QueryDevices(
                "GetDeviceById",
                SelectFields + @"
WHERE device_id = @deviceId",
                new DatabaseParameter("@deviceId", deviceId));
            return rows.Count == 0 ? null : rows[0];
        }

        public bool ExistsDeviceId(int deviceId)
        {
            return ScalarExists("ExistsDeviceId", "SELECT device_id FROM dbo.devices WHERE device_id = @deviceId", new DatabaseParameter("@deviceId", deviceId));
        }

        public bool ExistsIpAddress(string ipAddress)
        {
            return ScalarExists("ExistsDeviceIp", "SELECT device_id FROM dbo.devices WHERE ip_address = @ipAddress", new DatabaseParameter("@ipAddress", ipAddress ?? string.Empty));
        }

        public DatabaseWriteResult InsertDevice(DeviceRecord record)
        {
            if (record == null)
            {
                return DatabaseWriteResult.Failed("INVALID_ARGUMENT", "Device record is required.");
            }

            var command = @"
INSERT INTO dbo.devices
(
    device_id,
    device_name,
    description,
    ip_address,
    port,
    username,
    [password],
    status
)
VALUES
(
    @deviceId,
    @deviceName,
    @description,
    @ipAddress,
    @port,
    @username,
    @password,
    @status
);";
            return ExecuteWrite(
                "InsertDevice",
                command,
                new DatabaseParameter("@deviceId", record.DeviceId),
                new DatabaseParameter("@deviceName", record.DeviceName),
                new DatabaseParameter("@description", string.IsNullOrEmpty(record.Description) ? null : record.Description),
                new DatabaseParameter("@ipAddress", record.IpAddress),
                new DatabaseParameter("@port", record.Port.ToString()),
                new DatabaseParameter("@username", record.Username),
                new DatabaseParameter("@password", record.Password),
                new DatabaseParameter("@status", record.Enabled ? 1 : 0));
        }

        public DatabaseWriteResult DeleteDevice(int deviceId)
        {
            return ExecuteWrite(
                "DeleteDevice",
                "DELETE FROM dbo.devices WHERE device_id = @deviceId;",
                new DatabaseParameter("@deviceId", deviceId));
        }

        public DatabaseWriteResult UpdateLastUsedTime(int deviceId)
        {
            return ExecuteWrite(
                "UpdateDeviceLastUsedTime",
                "UPDATE dbo.devices SET last_used_time = SYSDATETIME() WHERE device_id = @deviceId;",
                new DatabaseParameter("@deviceId", deviceId));
        }

        private IReadOnlyList<DeviceRecord> QueryDevices(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            var rows = database.ExecuteQuery(operationName, commandText, parameters);
            var records = new List<DeviceRecord>();
            foreach (var row in rows)
            {
                records.Add(Map(row));
            }

            return records;
        }

        private bool ScalarExists(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            var rows = database.ExecuteQuery(operationName, commandText, parameters);
            return rows.Count > 0;
        }

        private DatabaseWriteResult ExecuteWrite(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            var record = database.ExecuteNonQuery(operationName, commandText, parameters);
            if (record.Error != null)
            {
                return DatabaseWriteResult.Failed("DB_ERROR", record.Error.Message);
            }

            return DatabaseWriteResult.Ok(record.RowsAffected, operationName + " succeeded.");
        }

        private static DeviceRecord Map(IReadOnlyDictionary<string, object> row)
        {
            var record = new DeviceRecord
            {
                DeviceId = ToInt(row, "device_id"),
                DeviceName = ToString(row, "device_name"),
                Description = ToString(row, "description"),
                IpAddress = ToString(row, "ip_address"),
                Port = ToPort(row, "port"),
                Username = ToString(row, "username", "admin"),
                Password = ToString(row, "password"),
                Enabled = ToInt(row, "status") == 1,
                LastUsedAt = ToDateTime(row, "last_used_time")
            };
            return record;
        }

        private static int ToPort(IReadOnlyDictionary<string, object> row, string key)
        {
            var text = ToString(row, key, "8000");
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }

        private static int ToInt(IReadOnlyDictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            if (value is int)
            {
                return (int)value;
            }

            if (value is byte)
            {
                return (byte)value;
            }

            if (value is short)
            {
                return (short)value;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }

        private static string ToString(IReadOnlyDictionary<string, object> row, string key, string defaultValue = "")
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToString(value) ?? defaultValue;
        }

        private static DateTime? ToDateTime(IReadOnlyDictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || value == null)
            {
                return null;
            }

            if (value is DateTime)
            {
                return (DateTime)value;
            }

            DateTime parsed;
            return DateTime.TryParse(Convert.ToString(value), out parsed) ? parsed : (DateTime?)null;
        }
    }
}
