using System;
using System.Security.Cryptography;
using System.Text;

namespace ControlDoor.FaceEvents
{
    public sealed class EventIdGenerator
    {
        public long CreateFromSerial(int deviceId, long serialNo)
        {
            if (serialNo <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(serialNo), "serialNo must be greater than 0.");
            }

            var normalizedDevice = Math.Max(0, deviceId);
            var value = normalizedDevice * 10000000000L + serialNo;
            if (value > 0)
            {
                return value;
            }

            return serialNo;
        }

        public long CreateFallback(string deviceKey, DateTime eventTime, string employeeId, string cardNo, int? eventType)
        {
            var key = string.Join("|", new[]
            {
                deviceKey ?? string.Empty,
                eventTime.ToString("O"),
                employeeId ?? string.Empty,
                cardNo ?? string.Empty,
                eventType.HasValue ? eventType.Value.ToString() : string.Empty
            });

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var value = BitConverter.ToInt64(hash, 0) & long.MaxValue;
                return value == 0 ? 1 : value;
            }
        }
    }
}
