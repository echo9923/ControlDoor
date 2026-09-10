using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ControlDoor.Configuration;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.FaceEvents;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Permissions;
using ControlDoor.Runtime;


namespace ControlEntradaSalida.Tests
{
    public static class ReviewRegressionTests
    {
        private static PermissionSyncGrpcService Service(Stage4Fixture fixture, IDeviceOperationRetryWriter writer)
        {
            return new PermissionSyncGrpcService(fixture.Registry, fixture.Dispatcher, fixture.Gateway, writer);
        }

        private static void Prepare(Stage4Fixture fixture, bool online)
        {
            fixture.Options.AlarmEnabled = false;
            fixture.AddRecord();
            fixture.Lifecycle.LoadEnabledDevices(false);
            if (online) Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "review").Success);
        }

        [TestCase]
        public static void PermissionSync_PersonFails_PreservesFaceIntent()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                fixture.Gateway.ConfigureException("UpsertPersonAsync", new DeviceGatewayException("UpsertPerson", SdkError.FromCode(7)));
                var writer = new RetryWriter();
                var response = Service(fixture, writer).SyncPersons("{\"people\":[{\"employeeId\":\"E1\",\"name\":\"Test\",\"faceBase64\":\"/9gBAv/Z\"}]}");
                Assert.Equal(2, writer.Intents.Count);
                Assert.Equal("SyncPerson", writer.Intents[0].Operation);
                Assert.Equal("UploadFace", writer.Intents[1].Operation);
            }
        }

        [TestCase]
        public static void PermissionSync_PersistenceFails_ReportsFailedWithoutQueue()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, false);
                var writer = new RetryWriter { Fail = true };
                var response = Service(fixture, writer).SyncPersonsToDevices("{\"deviceIds\":[1],\"people\":[{\"employeeId\":\"E1\",\"name\":\"Test\"}]}");
                Assert.Contains("DB_ERROR", response);
                Assert.Contains("\"queued\":0", response);
                Assert.Contains("\"success\":false", response);
                Assert.Contains("\"failed\":1", response);
                Assert.False(response.Contains("\"dbErrors\":[]"));
            }
        }

        [TestCase]
        public static void Lifecycle_QueuedLoginAfterDisconnect_RemainsDisconnected()
        {
            using (var fixture = new Stage4Fixture())
            using (var entered = new ManualResetEventSlim())
            {
                Prepare(fixture, false);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var blocker = new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "Blocker", async context =>
                {
                    entered.Set();
                    await release.Task.ConfigureAwait(false);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "done", DeviceConnectionStatus.Offline, DateTime.Now, DateTime.Now);
                });
                fixture.Dispatcher.Submit(blocker);
                Assert.True(entered.Wait(2000));
                fixture.Lifecycle.SubmitLogin(1, false, "queued-before-disconnect");
                var disconnect = Task.Run(() => fixture.Lifecycle.DisconnectDevice(1, "manual-disconnect"));
                try
                {
                    Assert.True(SpinWait.SpinUntil(() => fixture.Dispatcher.GetWorkerSnapshots().Any(x => x.QueueLength >= 2), 2000));
                }
                finally { release.TrySetResult(true); }
                Assert.True(disconnect.GetAwaiter().GetResult().Success);
                DrainWorker(fixture);
                Assert.False(fixture.Registry.TryGetByDeviceId(1).Snapshot.IsConnected);
                Assert.Equal(0, fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync"));
            }
        }

        [TestCase]
        public static void Lifecycle_PassiveOffline_LogsOutBeforeNextLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                fixture.Gateway.ConfigureException("GetDeviceInfoAsync", new DeviceGatewayException("Info", SdkError.FromCode(7)));
                for (var i = 0; i < 3; i++) fixture.Lifecycle.SubmitHealthCheck(1, true, "review-health");
                Assert.False(fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId.HasValue);
                Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "next-login").Success);
                var logins = fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync");
                var logouts = fixture.Gateway.Calls.Count(x => x.MethodName == "LogoutAsync");
                Assert.Equal(2, logins);
                Assert.Equal(1, logouts);
                Assert.False(fixture.Registry.TryGetByDeviceId(1).Snapshot.StaleSdkUserId.HasValue);
            }
        }

        [TestCase]
        public static void SdkWrapper_LogoutFailure_PropagatesError()
        {
            var native = new NativeStub();
            using (var gateway = new HikvisionSdkWrapper(native))
            {
                try
                {
                    gateway.LogoutAsync(new LogoutRequest { UserId = 1 }).GetAwaiter().GetResult();
                    throw new Exception("Expected logout error.");
                }
                catch (DeviceGatewayException ex)
                {
                    Assert.Equal(7, ex.Error.Code);
                }
                Assert.True(native.LogoutCalled);
            }
        }

        [TestCase]
        public static void FaceEvents_ContinuousLowVolume_FlushesBeforeQueueBecomesIdle()
        {
            var processor = new CountingProcessor();
            var context = new BackgroundTaskContext("review", CancellationToken.None, null);
            using (var service = new FaceEventIngestionService(new FaceEventLoggingOptions { BatchSize = 50, FlushIntervalMs = 500 }, processor))
            {
                service.StartAsync(context).GetAwaiter().GetResult();
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 8; i++)
                {
                    service.TryEnqueue(new RawAcsAlarmEvent { DeviceId = 1 });
                    Thread.Sleep(200);
                }
                Assert.True(Volatile.Read(ref processor.Count) > 0, "A continuously populated queue must flush by the batch deadline.");
                service.StopAsync(context).GetAwaiter().GetResult();
                Assert.Equal(8, processor.Count);
            }
        }

        [TestCase]
        public static void Retry_OnlineSuccess_SkipsPreviouslyClaimedIntent()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, false);
                var store = new InMemoryRetryExecutionStore();
                var service = Service(fixture, store);
                service.SyncPersonsToDevices(PersonRequest("OldName"));
                var oldState = store.Snapshot;
                Assert.NotNull(oldState);
                Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "reconnect").Success);
                var response = service.SyncPersonsToDevices(PersonRequest("NewName"));
                Assert.Contains("\"code\":\"OK\"", response);
                var execution = new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway, isCurrent: store.IsCurrent)
                    .ExecuteAsync(new RetryCommandPlanner().Plan(oldState), "old-retry", CancellationToken.None).GetAwaiter().GetResult();
                Assert.Equal("SUPERSEDED", execution.Code);
                var names = fixture.Gateway.Calls.Where(x => x.MethodName == "UpsertPersonAsync")
                    .Select(x => ((UpsertPersonRequest)x.Request).Person.Name).ToArray();
                Assert.Equal(1, names.Length);
                Assert.Equal("NewName", names[0]);
            }
        }

        [TestCase]
        public static void PermissionSync_OnlinePersistenceFails_DoesNotTouchDevice()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                var store = new InMemoryRetryExecutionStore { FailWrites = true };
                var response = Service(fixture, store).SyncPersonsToDevices(PersonRequest("NewName"));
                Assert.Contains("\"failed\":1", response);
                Assert.Contains("\"queued\":0", response);
                Assert.False(response.Contains("\"dbErrors\":[]"));
                Assert.Equal(0, fixture.Gateway.Calls.Count(x => x.MethodName == "UpsertPersonAsync"));
            }
        }

        [TestCase]
        public static void PermissionSync_PersistedPersonFails_RetryContainsPersonThenFace()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                fixture.Gateway.ConfigureException("UpsertPersonAsync", new DeviceGatewayException("UpsertPerson", SdkError.FromCode(7)));
                var store = new InMemoryRetryExecutionStore();
                var response = Service(fixture, store).SyncPersons("{\"people\":[{\"employeeId\":\"E1\",\"name\":\"Test\",\"faceBase64\":\"/9gBAv/Z\"}]}");
                Assert.Contains("\"queued\":1", response);
                Assert.True(store.Snapshot.PersonPending);
                Assert.True(store.Snapshot.FacePending);
                var plan = new RetryCommandPlanner().Plan(store.Snapshot);
                Assert.Equal(RetryOperation.Person, plan.Steps[0].Operation);
                Assert.Equal(RetryOperation.Face, plan.Steps[1].Operation);
                fixture.Gateway.ConfigureResult("UpsertPersonAsync", 0);
                var result = new RetryExecutionCoordinator(fixture.Dispatcher, fixture.Gateway, isCurrent: store.IsCurrent)
                    .ExecuteAsync(plan, "person-face-retry", CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(result.AllSucceeded);
                var writes = fixture.Gateway.Calls.Where(x => x.MethodName == "UpsertPersonAsync" || x.MethodName == "UploadFaceAsync").ToList();
                Assert.Equal("UpsertPersonAsync", writes[writes.Count - 2].MethodName);
                Assert.Equal("UploadFaceAsync", writes[writes.Count - 1].MethodName);
            }
        }

        [TestCase]
        public static void PermissionSync_OldFailureAfterNewIntent_DoesNotRegisterOldIntentAgain()
        {
            using (var fixture = new Stage4Fixture())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Prepare(fixture, true);
                var store = new InMemoryRetryExecutionStore();
                var service = Service(fixture, store);
                fixture.Gateway.ConfigureResult("UpsertPersonAsync", request =>
                {
                    if (((UpsertPersonRequest)request).Person.Name == "OldName")
                    {
                        entered.Set();
                        if (!release.Wait(5000)) throw new TimeoutException("Test barrier timed out.");
                        throw new DeviceGatewayException("UpsertPerson", SdkError.FromCode(7));
                    }
                    return 0;
                });
                var oldRequest = Task.Run(() => service.SyncPersonsToDevices(PersonRequest("OldName")));
                Assert.True(entered.Wait(2000));
                var newRequest = Task.Run(() => service.SyncPersonsToDevices(PersonRequest("NewName")));
                try
                {
                    Assert.True(SpinWait.SpinUntil(() => store.Snapshot?.PersonPayloadJson.Contains("NewName") == true, 2000));
                }
                finally
                {
                    release.Set();
                }
                oldRequest.GetAwaiter().GetResult();
                Assert.Contains("\"code\":\"OK\"", newRequest.GetAwaiter().GetResult());
                Assert.Equal(null, store.Snapshot);
                var names = fixture.Gateway.Calls.Where(x => x.MethodName == "UpsertPersonAsync")
                    .Select(x => ((UpsertPersonRequest)x.Request).Person.Name).ToArray();
                Assert.Equal("NewName", names.Last());
            }
        }

        [TestCase]
        public static void Lifecycle_StaleLogoutFails_RetainsSessionWithoutNewLogin()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                var userId = fixture.Registry.TryGetByDeviceId(1).Snapshot.SdkUserId;
                fixture.Registry.MarkDisconnected(1, null, DateTime.Now, DeviceConnectionStatus.Offline);
                fixture.Gateway.ConfigureException("LogoutAsync", new DeviceGatewayException("Logout", SdkError.FromCode(7)));
                Assert.False(fixture.Lifecycle.SubmitLogin(1, true, "reconnect").Success);
                Assert.Equal(userId, fixture.Registry.TryGetByDeviceId(1).Snapshot.StaleSdkUserId);
                Assert.Equal(1, fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync"));
                fixture.Gateway.ConfigureResult("LogoutAsync", 0);
                Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "reconnect-again").Success);
                Assert.Equal(2, fixture.Gateway.Calls.Count(x => x.MethodName == "LoginAsync"));
            }
        }

        [TestCase]
        public static void Lifecycle_StaleSessionAlreadyGone_AllowsReconnect()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, true);
                fixture.Registry.MarkDisconnected(1, null, DateTime.Now, DeviceConnectionStatus.Offline);
                fixture.Gateway.ConfigureException("LogoutAsync", new DeviceGatewayException("Logout", SdkError.FromCode(47)));
                Assert.True(fixture.Lifecycle.SubmitLogin(1, true, "reconnect").Success);
                Assert.False(fixture.Registry.TryGetByDeviceId(1).Snapshot.StaleSdkUserId.HasValue);
            }
        }

        [TestCase]
        public static void PermissionSync_OfflinePersonWithFace_PersistsOneCombinedIntent()
        {
            using (var fixture = new Stage4Fixture())
            {
                Prepare(fixture, false);
                var store = new InMemoryRetryExecutionStore();
                var response = Service(fixture, store).SyncPersons("{\"people\":[{\"employeeId\":\"E1\",\"name\":\"Test\",\"faceBase64\":\"/9gBAv/Z\"}]}");
                Assert.Contains("\"queued\":1", response);
                Assert.True(store.Snapshot.PersonPending);
                Assert.True(store.Snapshot.FacePending);
                Assert.Equal(1, store.WriteCount);
            }
        }

        private static string PersonRequest(string name)
        {
            return new JavaScriptSerializer().Serialize(new { deviceIds = new[] { 1 }, people = new[] { new { employeeId = "E1", name } } });
        }

        private static void DrainWorker(Stage4Fixture fixture)
        {
            fixture.Dispatcher.SubmitAndWaitAsync(new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "Barrier",
                context => Task.FromResult(DeviceTaskResult.FromTask(context.Task, true, "OK", "", DeviceConnectionStatus.Offline, DateTime.Now, DateTime.Now)))
            { Priority = DeviceTaskPriority.Retry, AllowWhenManualDisconnected = true }).GetAwaiter().GetResult();
        }

        [TestCase]
        public static void Interlock_RestoreOvertakesQueuedClose_SkipsExpiredClose()
        {
            using (var fixture = new Stage9Fixture(windowSeconds: 1))
            using (var entered = new ManualResetEventSlim())
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var blocker = new DeviceSdkTask(fixture.DoorDeviceId, DeviceTaskType.HealthCheck, "Blocker", async context =>
                {
                    entered.Set();
                    await release.Task.ConfigureAwait(false);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "done", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now);
                });
                fixture.Dispatcher.Submit(blocker);
                Assert.True(entered.Wait(2000));
                var now = DateTime.Now;
                fixture.EmitAiopAlarm(fixture.CameraIp);
                Assert.Equal(1, fixture.Service.ProcessEvents(now).AlwaysCloseSubmissions);
                var expire = Task.Run(() => fixture.Service.ExpireWindows(now.AddSeconds(2)));
                try
                {
                    Assert.True(SpinWait.SpinUntil(() => fixture.Dispatcher.GetWorkerSnapshots().Any(x => x.QueueLength >= 2), 2000));
                }
                finally { release.TrySetResult(true); }
                expire.GetAwaiter().GetResult();
                fixture.Dispatcher.SubmitAndWaitAsync(new DeviceSdkTask(fixture.DoorDeviceId, DeviceTaskType.HealthCheck, "Barrier",
                    context => Task.FromResult(DeviceTaskResult.FromTask(context.Task, true, "OK", "", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now)))
                { Priority = DeviceTaskPriority.Retry }).GetAwaiter().GetResult();
                var commands = fixture.Gateway.Calls.Where(x => x.MethodName == "ControlGatewayAsync").Select(x => ((GateControlRequest)x.Request).Command).ToArray();
                Assert.Equal(GateControlCommand.Restore, commands[0]);
                Assert.Equal(1, commands.Length);
                Assert.Equal(0, fixture.TargetManager.GetOutstandingTargets().Count);
            }
        }

        private sealed class RetryWriter : IDeviceOperationRetryWriter
        {
            public readonly List<DeviceOperationRetryIntent> Intents = new List<DeviceOperationRetryIntent>();
            public bool Fail;
            public DeviceOperationRetryWriteResult UpsertIntent(DeviceOperationRetryIntent intent)
            {
                Intents.Add(intent);
                return Fail ? DeviceOperationRetryWriteResult.Failed(intent, "DB_ERROR", "simulated database failure") : DeviceOperationRetryWriteResult.Ok(intent);
            }
        }

        private sealed class CountingProcessor : IAcsFaceEventProcessor
        {
            public int Count;
            public FaceEventProcessResult Process(RawAcsAlarmEvent item)
            {
                Interlocked.Increment(ref Count);
                return FaceEventProcessResult.Ok("OK", "done");
            }
            public IReadOnlyList<FaceEventBatchItemResult> ProcessBatch(IReadOnlyList<RawAcsAlarmEvent> items)
            {
                Interlocked.Add(ref Count, items.Count);
                return items.Select(x => FaceEventBatchItemResult.Ok(1)).ToList();
            }
        }

        private sealed class NativeStub : IHikvisionSdkNativeClient
        {
            public bool LogoutCalled;
            public bool Init() { return true; }
            public bool Cleanup() { return true; }
            public int Login(LoginRequest request, out DeviceInfo info) { info = new DeviceInfo(); return 1; }
            public bool Logout(int userId) { LogoutCalled = true; return false; }
            public bool SetMessageCallback(HikvisionAlarmNativeCallback callback) { return true; }
            public int SetupAlarm(int userId, int level, int alarmInfoType, int deployType) { return 1; }
            public bool CloseAlarm(int handle) { return true; }
            public bool GetAcsWorkStatus(int userId, int channel, out AcsWorkStatus status) { status = new AcsWorkStatus(); return true; }
            public bool ControlGateway(int userId, int index, GateControlCommand command) { return true; }
            public bool CaptureJpegPicture(int userId, int channel, int quality, string path) { return true; }
            public bool StandardXmlConfig(int userId, string url, string input, out string output) { output = "{}"; return true; }
            public int UploadFaceData(int userId, string url, string json, byte[] picture, CancellationToken cancellationToken, out string response) { response = "{}"; return 1000; }
            public int CaptureFace(int userId, int attempts, int interval, CancellationToken token, out byte[] image, out byte quality, out int error) { image = new byte[0]; quality = 0; error = 0; return 1000; }
            public int GetLastError() { return 7; }
            public string GetErrorMessage(int code) { return "simulated failure"; }
        }
    }
}
