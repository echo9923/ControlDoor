using System;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7AcsEventParserTests
    {
        [TestCase]
        public static void AcsEventParser_StandardSuccessEvent_MapsFields()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["dwSerialNo"] = "345";
            raw.Values["dwEmployeeNo"] = "10001";
            raw.Values["cardNo"] = "C100";
            raw.Values["dwDoorNo"] = "2";
            raw.Values["dwMinor"] = "75";
            raw.Values["success"] = "true";
            raw.Values["eventTime"] = "2026-06-13T08:09:10";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal(70000000345L, result.Event.EventId);
            Assert.False(result.Event.EventIdGenerated);
            Assert.Equal("10001", result.Event.EmployeeId);
            Assert.Equal("C100", result.Event.CardNo);
            Assert.Equal(new DateTime(2026, 6, 13, 8, 9, 10), result.Event.EventTime);
            Assert.Equal(2, result.Event.Direction);
            Assert.Equal("Front Gate", result.Event.DeviceName);
            Assert.Equal("SN-7", result.Event.DeviceSn);
            Assert.Equal(75, result.Event.EventType.Value);
            Assert.Equal("认证成功", result.Event.AuthResult);
            Assert.Contains("\"source\":\"Realtime\"", result.Event.RawPayload);
        }

        [TestCase]
        public static void AcsEventParser_FailedAuthEvent_IsStillParsed()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10002";
            raw.Values["dwSerialNo"] = "346";
            raw.Values["success"] = "false";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("认证失败", result.Event.AuthResult);
        }

        [TestCase]
        public static void AcsEventParser_MissingEmployeeAndCard_ReturnsInvalid()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();

            var result = parser.Parse(raw);

            Assert.False(result.Success);
            Assert.Equal("MISSING_PERSON_KEY", result.Code);
        }

        [TestCase]
        public static void AcsEventParser_UnresolvedDevice_ReturnsInvalid()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.DeviceId = 0;
            raw.Values["employeeId"] = "10001";

            var result = parser.Parse(raw);

            Assert.False(result.Success);
            Assert.Equal("DEVICE_UNRESOLVED", result.Code);
        }

        [TestCase]
        public static void EventIdGenerator_Fallback_IsStable()
        {
            var generator = new EventIdGenerator();
            var time = new DateTime(2026, 6, 13, 8, 9, 10);

            var first = generator.CreateFallback("SN-7", time, "10001", "C100", 75);
            var second = generator.CreateFallback("SN-7", time, "10001", "C100", 75);

            Assert.Equal(first, second);
            Assert.True(first > 0);
        }

        [TestCase]
        public static void AcsEventParser_OfflineSource_IsIncludedInRawPayload()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Source = AcsAlarmEventSource.OfflineUpload;
            raw.CurrentEventFlag = 2;
            raw.Values["employeeId"] = "10001";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal(AcsAlarmEventSource.OfflineUpload, result.Event.Source);
            Assert.Contains("\"source\":\"OfflineUpload\"", result.Event.RawPayload);
            Assert.Contains("\"byCurrentEvent\":2", result.Event.RawPayload);
        }

        private static RawAcsAlarmEvent NewRawEvent()
        {
            return new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 6, 13, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                DeviceName = "Front Gate",
                DeviceSerialNo = "SN-7",
                Source = AcsAlarmEventSource.Realtime,
                PictureBytes = new byte[] { 1, 2, 3 },
                RawSummary = "length=120"
            };
        }
    }
}
