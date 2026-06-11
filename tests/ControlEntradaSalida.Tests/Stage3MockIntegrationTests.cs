using System;
using System.Linq;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage3MockIntegrationTests
    {
        [TestCase]
        public static void Stage2To3MockIntegration_LoginTask_RegistersSdkUserId()
        {
            var registry = RegisterDevice();
            var gateway = new MockHikvisionGateway();
            var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
            try
            {
                var task = new DeviceSdkTask(1, DeviceTaskType.Login, "Login", async context =>
                {
                    var device = context.SnapshotBeforeExecution;
                    var login = await gateway.LoginAsync(new LoginRequest
                    {
                        IpAddress = device.IpAddress,
                        Port = device.Port,
                        UserName = "admin",
                        Password = "12345"
                    }, context.CancellationToken).ConfigureAwait(false);

                    context.Registry.RegisterSdkUserId(context.Task.DeviceId, login.UserId, login.DeviceInfo.SerialNumber, DateTime.Now);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "login ok", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now);
                });

                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                var snapshot = registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(result.Success);
                Assert.Equal(DeviceConnectionStatus.Online, snapshot.Status);
                Assert.True(snapshot.SdkUserId.HasValue);
                Assert.Equal(snapshot.SdkUserId.Value, registry.TryGetBySdkUserId(snapshot.SdkUserId.Value).Snapshot.SdkUserId.Value);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                gateway.Dispose();
            }
        }

        [TestCase]
        public static void Stage2To3MockIntegration_LoginThenAlarm_RegistersAlarmHandle()
        {
            var registry = RegisterDevice();
            var gateway = new MockHikvisionGateway();
            var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
            try
            {
                var loginTask = LoginTask(gateway);
                var alarmTask = new DeviceSdkTask(1, DeviceTaskType.SetupAlarm, "SetupAlarm", async context =>
                {
                    var snapshot = context.SnapshotBeforeExecution;
                    var alarm = await gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = snapshot.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    context.Registry.RegisterAlarmHandle(context.Task.DeviceId, alarm.AlarmHandle, DateTime.Now);
                    return DeviceTaskResult.FromTask(context.Task, true, "OK", "alarm ok", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now);
                });

                dispatcher.SubmitAndWaitAsync(loginTask).GetAwaiter().GetResult();
                var result = dispatcher.SubmitAndWaitAsync(alarmTask).GetAwaiter().GetResult();
                var snapshot = registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(result.Success);
                Assert.True(snapshot.AlarmHandle.HasValue);
                Assert.Equal(snapshot.AlarmHandle.Value, registry.TryGetByAlarmHandle(snapshot.AlarmHandle.Value).Snapshot.AlarmHandle.Value);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                gateway.Dispose();
            }
        }

        [TestCase]
        public static void Stage2To3MockIntegration_DispatcherSerializesGatewayCallsForSameDevice()
        {
            var registry = RegisterDevice();
            var gateway = new MockHikvisionGateway();
            var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
            try
            {
                dispatcher.SubmitAndWaitAsync(LoginTask(gateway)).GetAwaiter().GetResult();

                var addTask = new DeviceSdkTask(1, DeviceTaskType.SyncPerson, "AddPerson", async context =>
                {
                    await gateway.AddPersonAsync(new AddPersonRequest
                    {
                        UserId = context.SnapshotBeforeExecution.SdkUserId.Value,
                        Person = Person("10001")
                    }, context.CancellationToken).ConfigureAwait(false);
                    return Success(context);
                });
                var faceTask = new DeviceSdkTask(1, DeviceTaskType.UploadFace, "UploadFace", async context =>
                {
                    await gateway.UploadFaceAsync(new UploadFaceRequest
                    {
                        UserId = context.SnapshotBeforeExecution.SdkUserId.Value,
                        Face = Face("10001")
                    }, context.CancellationToken).ConfigureAwait(false);
                    return Success(context);
                });

                var add = dispatcher.SubmitAndWaitAsync(addTask);
                var face = dispatcher.SubmitAndWaitAsync(faceTask);
                Task.WaitAll(add, face);

                Assert.True(add.Result.Success);
                Assert.True(face.Result.Success);
                var gatewayCalls = gateway.Calls.Where(call => call.MethodName == "AddPersonAsync" || call.MethodName == "UploadFaceAsync").ToList();
                Assert.Equal("AddPersonAsync", gatewayCalls[0].MethodName);
                Assert.Equal("UploadFaceAsync", gatewayCalls[1].MethodName);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                gateway.Dispose();
            }
        }

        [TestCase]
        public static void Stage2To3MockIntegration_GatewayErrorMarksRuntimeDisconnected()
        {
            var registry = RegisterDevice();
            var gateway = new MockHikvisionGateway();
            gateway.ConfigureException("LoginAsync", new DeviceGatewayException("Login", SdkError.FromCode(1)));
            var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
            try
            {
                var task = new DeviceSdkTask(1, DeviceTaskType.Login, "Login", async context =>
                {
                    try
                    {
                        await gateway.LoginAsync(new LoginRequest
                        {
                            IpAddress = context.SnapshotBeforeExecution.IpAddress,
                            UserName = "admin",
                            Password = "wrong"
                        }, context.CancellationToken).ConfigureAwait(false);
                        return Success(context);
                    }
                    catch (DeviceGatewayException ex)
                    {
                        context.Registry.MarkDisconnected(
                            context.Task.DeviceId,
                            DeviceRuntimeError.Create("Login", "SDK_ERROR", ex.Error.Message, DateTime.Now, sdkErrorCode: ex.Error.Code, retryable: true),
                            DateTime.Now,
                            DeviceConnectionStatus.Faulted);

                        var result = DeviceTaskResult.FromTask(context.Task, false, "SDK_ERROR", ex.Error.Message, DeviceConnectionStatus.Faulted, DateTime.Now, DateTime.Now);
                        result.SdkErrorCode = ex.Error.Code;
                        result.Retryable = true;
                        return result;
                    }
                });

                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                var snapshot = registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(result.Success);
                Assert.Equal(1, result.SdkErrorCode.Value);
                Assert.Equal(DeviceConnectionStatus.Faulted, snapshot.Status);
                Assert.Equal("SDK_ERROR", snapshot.LastErrorCode);
                Assert.True(snapshot.LastError.Retryable);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                gateway.Dispose();
            }
        }

        [TestCase]
        public static void Stage2To3MockIntegration_AlarmCallbackCanResolveDeviceByAlarmHandle()
        {
            var registry = RegisterDevice();
            var gateway = new MockHikvisionGateway();
            var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
            AlarmEventData captured = null;
            try
            {
                dispatcher.SubmitAndWaitAsync(LoginTask(gateway)).GetAwaiter().GetResult();
                dispatcher.SubmitAndWaitAsync(new DeviceSdkTask(1, DeviceTaskType.SetupAlarm, "SetupAlarm", async context =>
                {
                    var alarm = await gateway.SetAlarmAsync(new AlarmSetupRequest { UserId = context.SnapshotBeforeExecution.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    context.Registry.RegisterAlarmHandle(context.Task.DeviceId, alarm.AlarmHandle, DateTime.Now);
                    return Success(context);
                })).GetAwaiter().GetResult();
                var alarmHandle = registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.Value;
                gateway.OnAlarmEvent += (sender, data) =>
                {
                    var lookup = registry.TryGetByAlarmHandle(data.AlarmHandle);
                    if (lookup.Found)
                    {
                        captured = data;
                    }
                };

                gateway.EmitAlarm(new AlarmEventData
                {
                    AlarmHandle = alarmHandle,
                    Command = 0x5002,
                    EventType = "COMM_ALARM_ACS",
                    EmployeeId = "10001",
                    RawPayload = Stage3TestReflection.JpegBytes()
                });

                Assert.NotNull(captured);
                Assert.Equal(alarmHandle, captured.AlarmHandle);
                Assert.Equal("10001", captured.EmployeeId);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                gateway.Dispose();
            }
        }

        [TestCase]
        public static void Stage2To3MockIntegration_CapabilityProbeStoresRuntimeCapabilities()
        {
            var registry = RegisterDevice();
            var gateway = new MockHikvisionGateway();
            var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
            try
            {
                dispatcher.SubmitAndWaitAsync(LoginTask(gateway)).GetAwaiter().GetResult();
                var task = new DeviceSdkTask(1, DeviceTaskType.ProbeCapabilities, "ProbeCapabilities", async context =>
                {
                    var caps = await gateway.GetDeviceCapabilitiesAsync(new DeviceCapabilitiesRequest { UserId = context.SnapshotBeforeExecution.SdkUserId.Value }, context.CancellationToken).ConfigureAwait(false);
                    var deviceCaps = new ControlDoor.Devices.Runtime.DeviceCapabilities
                    {
                        Known = caps.Known,
                        SupportsAcs = caps.SupportsAcs,
                        SupportsAlarm = caps.SupportsAlarm,
                        SupportsPersonConfig = caps.SupportsPersonConfig,
                        SupportsFaceConfig = caps.SupportsFaceConfig,
                        SupportsFaceCapture = caps.SupportsFaceCapture,
                        SupportsHistoryEventQuery = caps.SupportsHistoryEventQuery,
                        SupportsIsapi = caps.SupportsIsapi,
                        LastCheckedAt = DateTime.Now
                    };
                    context.Registry.UpdateCapabilities(context.Task.DeviceId, deviceCaps, DateTime.Now);
                    return Success(context);
                });

                var result = dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();
                var snapshot = registry.TryGetByDeviceId(1).Snapshot;

                Assert.True(result.Success);
                Assert.True(snapshot.Capabilities.Known);
                Assert.True(snapshot.Capabilities.SupportsAcs);
                Assert.True(snapshot.Capabilities.SupportsFaceConfig);
                Assert.True(snapshot.Capabilities.SupportsIsapi);
            }
            finally
            {
                dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                gateway.Dispose();
            }
        }

        private static DeviceRuntimeRegistry RegisterDevice()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
            var result = registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = 1,
                DeviceName = "Mock Door",
                IpAddress = "192.168.1.64",
                Port = 8000,
                Username = "admin",
                Password = "12345",
                Enabled = true
            });

            Assert.True(result.Success);
            return registry;
        }

        private static DeviceSdkTask LoginTask(MockHikvisionGateway gateway)
        {
            return new DeviceSdkTask(1, DeviceTaskType.Login, "Login", async context =>
            {
                var device = context.SnapshotBeforeExecution;
                var login = await gateway.LoginAsync(new LoginRequest
                {
                    IpAddress = device.IpAddress,
                    Port = device.Port,
                    UserName = "admin",
                    Password = "12345"
                }, context.CancellationToken).ConfigureAwait(false);

                context.Registry.RegisterSdkUserId(context.Task.DeviceId, login.UserId, login.DeviceInfo.SerialNumber, DateTime.Now);
                return Success(context);
            });
        }

        private static DeviceTaskResult Success(DeviceTaskContext context)
        {
            return DeviceTaskResult.FromTask(context.Task, true, "OK", "ok", DeviceConnectionStatus.Online, DateTime.Now, DateTime.Now);
        }

        private static PersonInfo Person(string employeeId)
        {
            return new PersonInfo
            {
                EmployeeId = employeeId,
                Name = "Test",
                CardNumber = "C" + employeeId
            };
        }

        private static FaceInfo Face(string employeeId)
        {
            return new FaceInfo
            {
                EmployeeId = employeeId,
                CardNumber = "C" + employeeId,
                FaceId = "F" + employeeId,
                ImageBytes = Stage3TestReflection.JpegBytes(),
                QualityScore = 90
            };
        }
    }
}
