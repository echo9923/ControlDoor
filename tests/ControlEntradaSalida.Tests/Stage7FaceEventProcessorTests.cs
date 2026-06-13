using System;
using System.Linq;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7FaceEventProcessorTests
    {
        [TestCase]
        public static void AcsFaceEventProcessor_RawEvent_ParsesAndInserts()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var processor = new AcsFaceEventProcessor(new AcsEventParser(), new FaceEventRepository(database, storage));

            var result = processor.Process(NewRawEvent());

            Assert.True(result.Success);
            Assert.Equal("INSERTED", result.Code);
            Assert.True(database.Commands.Any(item => item.OperationName == "InsertFaceEvent"));
        }

        [TestCase]
        public static void AcsFaceEventProcessor_Duplicate_IsSuccess()
        {
            var database = new RecordingDatabaseClient
            {
                FailOperationName = "InsertFaceEvent",
                FailSqlErrorNumber = 2627
            };
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var processor = new AcsFaceEventProcessor(new AcsEventParser(), new FaceEventRepository(database, storage));

            var result = processor.Process(NewRawEvent());

            Assert.True(result.Success);
            Assert.Equal("DUPLICATE", result.Code);
        }

        private static RawAcsAlarmEvent NewRawEvent()
        {
            var raw = new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 6, 13, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                DeviceName = "Front Gate",
                DeviceSerialNo = "SN-7",
                Source = AcsAlarmEventSource.Realtime,
                PictureBytes = new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9 },
                RawSummary = "length=120"
            };
            raw.Values["employeeId"] = "10001";
            raw.Values["dwSerialNo"] = "345";
            raw.Values["dwMinor"] = "75";
            raw.Values["success"] = "true";
            return raw;
        }
    }
}
