using System;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7AcsEventParserEdgeTests
    {
        [TestCase]
        public static void AcsEventParser_DirectionOutString_ReturnsExit()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";
            raw.Values["direction"] = "out";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal(2, result.Event.Direction);
        }

        [TestCase]
        public static void AcsEventParser_NoDirection_DefaultsToEntry()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal(1, result.Event.Direction);
        }

        [TestCase]
        public static void AcsEventParser_NoEventTime_FallsBackToReceivedAt()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal(raw.ReceivedAt, result.Event.EventTime);
        }

        [TestCase]
        public static void AcsEventParser_Pre1970EventTime_FallsBackToReceivedAt()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";
            raw.Values["eventTime"] = "1969-01-01T00:00:00";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal(raw.ReceivedAt, result.Event.EventTime);
        }

        [TestCase]
        public static void AcsEventParser_NoSuccess_UnknownAuthResult()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("认证结果未知", result.Event.AuthResult);
        }

        [TestCase]
        public static void AcsEventParser_NumericSuccess_TreatedAsUnknown()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";
            raw.Values["success"] = "1";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("认证结果未知", result.Event.AuthResult);
        }

        [TestCase]
        public static void AcsEventParser_DwEmployeeNoZero_IsPreservedAsEmployeeId()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["dwEmployeeNo"] = "0";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("0", result.Event.EmployeeId);
        }

        [TestCase]
        public static void AcsEventParser_ByEmployeeNoWithLeadingZero_IsPreserved()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["byEmployeeNo"] = "0976";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("0976", result.Event.EmployeeId);
        }

        [TestCase]
        public static void AcsEventParser_DwEmployeeNoWithLeadingZero_IsPreserved()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["dwEmployeeNo"] = "0976";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("0976", result.Event.EmployeeId);
        }

        [TestCase]
        public static void AcsEventParser_EmployeeIdWithLeadingZeros_IsPreserved()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "000976";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("000976", result.Event.EmployeeId);
        }

        [TestCase]
        public static void AcsEventParser_EmployeeIdTakesPrecedenceOverByEmployeeNo()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "000976";
            raw.Values["byEmployeeNo"] = "0976";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.Equal("000976", result.Event.EmployeeId);
        }

        [TestCase]
        public static void AcsEventParser_ZeroSerialNumber_GeneratesFallbackId()
        {
            var parser = new AcsEventParser();
            var raw = NewRawEvent();
            raw.Values["employeeId"] = "10001";
            raw.Values["dwSerialNo"] = "0";

            var result = parser.Parse(raw);

            Assert.True(result.Success);
            Assert.True(result.Event.EventIdGenerated);
            Assert.True(result.Event.EventId > 0);
        }

        [TestCase]
        public static void AcsEventParser_NullRawEvent_ReturnsInvalidArgument()
        {
            var parser = new AcsEventParser();

            var result = parser.Parse(null);

            Assert.False(result.Success);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
        }

        [TestCase]
        public static void EventIdGenerator_CreateFromSerial_NonPositive_Throws()
        {
            var generator = new EventIdGenerator();

            Stage3TestReflection.Expect<ArgumentOutOfRangeException>(() => generator.CreateFromSerial(7, 0));
            Stage3TestReflection.Expect<ArgumentOutOfRangeException>(() => generator.CreateFromSerial(7, -1));
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
