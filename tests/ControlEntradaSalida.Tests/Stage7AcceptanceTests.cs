using System;
using System.IO;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7AcceptanceTests
    {
        [TestCase]
        public static void FaceEventIngestionService_BackgroundConsumer_InsertsQueuedRealtimeEvent()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var processor = new AcsFaceEventProcessor(new AcsEventParser(), new FaceEventRepository(database, storage));
            var service = new FaceEventIngestionService(new FaceEventLoggingOptions { QueueCapacity = 10 }, processor);
            service.StartAsync(new BackgroundTaskContext("stage7-test", CancellationToken.None, null)).GetAwaiter().GetResult();
            try
            {
                var enqueue = service.TryEnqueue(NewRawEvent(AcsAlarmEventSource.Realtime, null));

                WaitUntil(() => database.Commands.Any(item => item.OperationName == "InsertFaceEvent"), "queued ACS event was not inserted.");
                Assert.True(enqueue.Accepted);
                Assert.True(database.Commands.Any(item => item.OperationName == "CheckFaceEventDuplicate"));
                Assert.True(database.Commands.Any(item => item.OperationName == "InsertFaceEvent"));
            }
            finally
            {
                service.StopAsync(new BackgroundTaskContext("stage7-test", CancellationToken.None, null)).GetAwaiter().GetResult();
                service.Dispose();
            }
        }

        [TestCase]
        public static void ConfigurationValidator_FaceEventLogging_InvalidValues_FallBack()
        {
            var validator = new ConfigurationValidator();
            var settings = new AppSettings();
            settings.Database.ConnectionString = "Server=.;Database=test;Trusted_Connection=True;";
            settings.FaceEventLogging.AlarmDeployType = 1;
            settings.FaceEventLogging.QueueCapacity = 0;
            settings.FaceEventLogging.SnapshotRootDirectory = "";

            var result = validator.Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(0, result.Settings.FaceEventLogging.AlarmDeployType);
            Assert.Equal(2000, result.Settings.FaceEventLogging.QueueCapacity);
            Assert.Equal("snapshots", result.Settings.FaceEventLogging.SnapshotRootDirectory);
            Assert.True(result.Warnings.Any(item => item.Contains("FaceEventLogging.AlarmDeployType")));
        }

        [TestCase]
        public static void Stage7FaceEventCode_DoesNotUseActiveHistoryQueryOrCheckpoint()
        {
            var root = FindRepositoryRoot();

            var faceEventsDirectory = Path.Combine(root, "src", "ControlDoor", "FaceEvents");
            var text = string.Join(Environment.NewLine, Directory.GetFiles(faceEventsDirectory, "*.cs").Select(File.ReadAllText));

            Assert.False(text.Contains("QueryEventRecordAsync"));
            Assert.False(text.Contains("EventQueryRequest"));
            Assert.False(text.Contains("NET_DVR_GET_ACS_EVENT"));
            Assert.False(text.Contains("face_event_checkpoint"));
        }

        [TestCase]
        public static void AcsFaceEventProcessor_OfflineUpload_InsertsAndMarksRawPayload()
        {
            var database = new RecordingDatabaseClient();
            var storage = new SnapshotStorage(TestWorkspace.Create());
            var processor = new AcsFaceEventProcessor(new AcsEventParser(), new FaceEventRepository(database, storage));

            var result = processor.Process(NewRawEvent(AcsAlarmEventSource.OfflineUpload, 2));

            Assert.True(result.Success);
            var insert = database.Commands.Single(item => item.OperationName == "InsertFaceEvent");
            Assert.Contains("OfflineUpload", insert.CommandText);
            Assert.False(database.Commands.Any(item => item.CommandText.Contains("face_event_checkpoint")));
        }

        private static RawAcsAlarmEvent NewRawEvent(AcsAlarmEventSource source, int? currentEventFlag)
        {
            var raw = new RawAcsAlarmEvent
            {
                ReceivedAt = new DateTime(2026, 6, 13, 10, 0, 0),
                Command = AcsAlarmEventRouter.CommAlarmAcs,
                DeviceId = 7,
                DeviceIp = "192.168.1.10",
                DeviceName = "Front Gate",
                DeviceSerialNo = "SN-7",
                Source = source,
                CurrentEventFlag = currentEventFlag,
                PictureBytes = new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9 },
                RawSummary = "length=120"
            };
            raw.Values["employeeId"] = "10001";
            raw.Values["dwSerialNo"] = currentEventFlag == 2 ? "346" : "345";
            raw.Values["dwMinor"] = "75";
            raw.Values["success"] = "true";
            return raw;
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(20);
            }

            Assert.True(condition(), message);
        }

        private static string FindRepositoryRoot()
        {
            var candidates = new[]
            {
                Directory.GetCurrentDirectory(),
                AppDomain.CurrentDomain.BaseDirectory
            };

            foreach (var candidate in candidates)
            {
                var root = candidate;
                while (!File.Exists(Path.Combine(root, "ControlEntradaSalida.sln")) && Directory.GetParent(root) != null)
                {
                    root = Directory.GetParent(root).FullName;
                }

                if (File.Exists(Path.Combine(root, "ControlEntradaSalida.sln")))
                {
                    return root;
                }
            }

            throw new DirectoryNotFoundException("ControlEntradaSalida.sln not found.");
        }
    }
}
