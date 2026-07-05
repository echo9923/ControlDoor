using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace ControlDoor.FaceEvents
{
    public sealed class AcsEventParser
    {
        private readonly EventIdGenerator eventIdGenerator;

        public AcsEventParser(EventIdGenerator eventIdGenerator = null)
        {
            this.eventIdGenerator = eventIdGenerator ?? new EventIdGenerator();
        }

        public AcsEventParseResult Parse(RawAcsAlarmEvent rawEvent)
        {
            if (rawEvent == null)
            {
                return AcsEventParseResult.Invalid("INVALID_ARGUMENT", "raw event is required");
            }

            if (rawEvent.DeviceId <= 0)
            {
                return AcsEventParseResult.Invalid("DEVICE_UNRESOLVED", "device could not be resolved");
            }

            var employeeId = FirstNonEmpty(
                GetValue(rawEvent, "employeeId"),
                GetValue(rawEvent, "byEmployeeNo"),
                GetValue(rawEvent, "EmployeeId"),
                GetValue(rawEvent, "dwEmployeeNo"));
            var cardNo = FirstNonEmpty(
                GetValue(rawEvent, "cardNo"),
                GetValue(rawEvent, "CardNumber"),
                GetValue(rawEvent, "byCardNo"));
            if (string.IsNullOrWhiteSpace(employeeId) && string.IsNullOrWhiteSpace(cardNo))
            {
                return AcsEventParseResult.Invalid("MISSING_PERSON_KEY", "employee id and card number are both empty");
            }

            var eventTime = ResolveEventTime(rawEvent);
            var eventType = ResolveInt(rawEvent, "dwMinor") ?? ResolveInt(rawEvent, "eventType");
            var serialNo = ResolveLong(rawEvent, "dwSerialNo");
            var eventIdGenerated = !serialNo.HasValue || serialNo.Value <= 0;
            var eventId = eventIdGenerated
                ? eventIdGenerator.CreateFallback(FirstNonEmpty(rawEvent.DeviceSerialNo, rawEvent.DeviceIp, rawEvent.DeviceId.ToString()), eventTime, employeeId, cardNo, eventType)
                : eventIdGenerator.CreateFromSerial(rawEvent.DeviceId, serialNo.Value);

            var direction = ResolveDirection(rawEvent);
            var success = ResolveBool(rawEvent, "success");
            var authResult = success.HasValue ? (success.Value ? "认证成功" : "认证失败") : "认证结果未知";
            var faceEvent = new AcsFaceEvent
            {
                EventId = eventId,
                EventIdGenerated = eventIdGenerated,
                EmployeeId = string.IsNullOrWhiteSpace(employeeId) ? cardNo : employeeId.Trim(),
                EventTime = eventTime,
                Direction = direction,
                DeviceName = rawEvent.DeviceName,
                DeviceSn = FirstNonEmpty(rawEvent.DeviceSerialNo, rawEvent.DeviceIp),
                DeviceId = rawEvent.DeviceId,
                DeviceIp = rawEvent.DeviceIp,
                CardNo = string.IsNullOrWhiteSpace(cardNo) ? null : cardNo.Trim(),
                EventType = eventType,
                AuthResult = authResult,
                PictureBytes = CopyBytes(rawEvent.PictureBytes),
                Source = rawEvent.Source
            };

            BuildRawPayload(rawEvent, faceEvent, eventTime != rawEvent.ReceivedAt);
            return AcsEventParseResult.Parsed(faceEvent);
        }

        private static DateTime ResolveEventTime(RawAcsAlarmEvent rawEvent)
        {
            var value = GetValue(rawEvent, "eventTime");
            DateTime parsed;
            if (!string.IsNullOrWhiteSpace(value) && DateTime.TryParse(value, out parsed) && parsed.Year >= 1970)
            {
                return parsed;
            }

            return rawEvent.ReceivedAt == default ? DateTime.Now : rawEvent.ReceivedAt;
        }

        private static int ResolveDirection(RawAcsAlarmEvent rawEvent)
        {
            var direction = GetValue(rawEvent, "direction");
            if (string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(direction, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(direction, "2", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            var doorNo = ResolveInt(rawEvent, "dwDoorNo");
            if (doorNo == 2)
            {
                return 2;
            }

            return 1;
        }

        private static void BuildRawPayload(RawAcsAlarmEvent rawEvent, AcsFaceEvent faceEvent, bool eventTimeResolved)
        {
            faceEvent.RawPayloadFields["source"] = faceEvent.Source.ToString();
            faceEvent.RawPayloadFields["deviceId"] = rawEvent.DeviceId;
            faceEvent.RawPayloadFields["deviceIp"] = rawEvent.DeviceIp ?? string.Empty;
            faceEvent.RawPayloadFields["eventType"] = faceEvent.EventType;
            faceEvent.RawPayloadFields["authResult"] = faceEvent.AuthResult ?? string.Empty;
            faceEvent.RawPayloadFields["sdkCommand"] = rawEvent.Command;
            faceEvent.RawPayloadFields["byCurrentEvent"] = rawEvent.CurrentEventFlag;
            faceEvent.RawPayloadFields["eventIdGenerated"] = faceEvent.EventIdGenerated;
            faceEvent.RawPayloadFields["eventTimeResolved"] = eventTimeResolved;
            faceEvent.RawPayloadFields["rawSummary"] = rawEvent.RawSummary ?? string.Empty;
            foreach (var item in rawEvent.Values)
            {
                if (!faceEvent.RawPayloadFields.ContainsKey(item.Key))
                {
                    faceEvent.RawPayloadFields[item.Key] = item.Value;
                }
            }

            faceEvent.RawPayload = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(faceEvent.RawPayloadFields);
        }

        private static string GetValue(RawAcsAlarmEvent rawEvent, string key)
        {
            if (rawEvent == null || rawEvent.Values == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string value;
            return rawEvent.Values.TryGetValue(key, out value) ? value : null;
        }

        private static int? ResolveInt(RawAcsAlarmEvent rawEvent, string key)
        {
            int value;
            return int.TryParse(GetValue(rawEvent, key), out value) ? (int?)value : null;
        }

        private static long? ResolveLong(RawAcsAlarmEvent rawEvent, string key)
        {
            long value;
            return long.TryParse(GetValue(rawEvent, key), out value) ? (long?)value : null;
        }

        private static bool? ResolveBool(RawAcsAlarmEvent rawEvent, string key)
        {
            bool value;
            if (bool.TryParse(GetValue(rawEvent, key), out value))
            {
                return value;
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static byte[] CopyBytes(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                return new byte[0];
            }

            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}
