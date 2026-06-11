using System;
using System.Linq;
using System.Xml.Linq;

namespace ControlDoor.Hikvision
{
    internal static class HikvisionXmlParser
    {
        public static DeviceCapabilities ParseCapabilities(string raw)
        {
            var capabilities = new DeviceCapabilities
            {
                Known = true,
                LastCheckedAt = DateTime.Now,
                RawCapabilities = raw
            };

            if (string.IsNullOrWhiteSpace(raw))
            {
                return capabilities;
            }

            var text = raw.ToLowerInvariant();
            capabilities.SupportsAcs = ContainsAny(text, "acs", "accesscontrol", "door");
            capabilities.SupportsAlarm = ContainsAny(text, "alarm", "setupalarm", "event");
            capabilities.SupportsPersonConfig = ContainsAny(text, "person", "user", "employee", "card");
            capabilities.SupportsFaceConfig = ContainsAny(text, "face", "facedata", "fdlib");
            capabilities.SupportsFaceCapture = ContainsAny(text, "captureface", "facecapture", "manualcapture");
            capabilities.SupportsHistoryEventQuery = ContainsAny(text, "eventsearch", "acsevent", "eventrecord");
            capabilities.SupportsIsapi = ContainsAny(text, "isapi", "/isapi", "capabilities");
            capabilities.SupportsAiop = ContainsAny(text, "aiop", "uploadaiop", "comm_upload_aiop_video");

            var doorCount = TryReadInt(raw, "doorNum", "doorCount", "doors");
            var channelCount = TryReadInt(raw, "channelNum", "channelCount", "channels");
            capabilities.DoorCount = doorCount;
            capabilities.ChannelCount = channelCount;
            capabilities.Model = TryReadString(raw, "model", "deviceModel");
            capabilities.FirmwareVersion = TryReadString(raw, "firmwareVersion", "firmware");

            return capabilities;
        }

        public static DeviceInfo ParseDeviceInfo(string raw)
        {
            return new DeviceInfo
            {
                Model = TryReadString(raw, "model", "deviceModel"),
                SerialNumber = TryReadString(raw, "serialNumber", "serialNo"),
                FirmwareVersion = TryReadString(raw, "firmwareVersion", "firmware"),
                DeviceName = TryReadString(raw, "deviceName", "name"),
                MacAddress = TryReadString(raw, "macAddress", "mac"),
                IpAddress = TryReadString(raw, "ipAddress", "ip"),
                DoorCount = TryReadInt(raw, "doorNum", "doorCount", "doors"),
                ChannelCount = TryReadInt(raw, "channelNum", "channelCount", "channels")
            };
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            return tokens.Any(token => value.Contains(token));
        }

        private static string TryReadString(string raw, params string[] names)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                var document = XDocument.Parse(raw);
                foreach (var name in names)
                {
                    var match = document
                        .Descendants()
                        .FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        return match.Value;
                    }
                }
            }
            catch
            {
                foreach (var name in names)
                {
                    var marker = "\"" + name + "\"";
                    var index = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        var colon = raw.IndexOf(':', index);
                        if (colon >= 0)
                        {
                            var start = raw.IndexOf('"', colon + 1);
                            if (start >= 0)
                            {
                                var end = raw.IndexOf('"', start + 1);
                                if (end > start)
                                {
                                    return raw.Substring(start + 1, end - start - 1);
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        private static int TryReadInt(string raw, params string[] names)
        {
            var text = TryReadString(raw, names);
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }
    }
}
