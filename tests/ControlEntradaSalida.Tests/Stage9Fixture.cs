using System;
using System.Linq;
using System.Threading;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Configuration;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    internal sealed class Stage9Fixture : IDisposable
    {
        private readonly Stage4Fixture inner;

        public Stage9Fixture(
            int doorDeviceId = 10,
            string doorIp = "10.0.0.10",
            string cameraIp = "10.0.0.5",
            int windowSeconds = 5,
            int restoreRetryIntervalMs = 1000,
            Func<DateTime> clock = null,
            string secondCameraIp = null,
            int[] doorNos = null,
            bool enabled = true)
        {
            inner = new Stage4Fixture();
            DoorDeviceId = doorDeviceId;
            DoorIp = doorIp;
            CameraIp = cameraIp;
            SecondCameraIp = secondCameraIp;

            AddDoorDevice(doorDeviceId, doorIp);

            var effectiveDoorNos = doorNos == null || doorNos.Length == 0
                ? new System.Collections.Generic.List<int> { 1 }
                : new System.Collections.Generic.List<int>(doorNos);

            var mappings = new System.Collections.Generic.List<CameraAlarmDoorInterlockMapping>
            {
                new CameraAlarmDoorInterlockMapping
                {
                    Camera = new InterlockCamera { Ip = cameraIp },
                    DoorDevice = new InterlockDoorDevice { Ip = doorIp },
                    DoorNos = effectiveDoorNos
                }
            };

            if (!string.IsNullOrEmpty(secondCameraIp))
            {
                mappings.Add(new CameraAlarmDoorInterlockMapping
                {
                    Camera = new InterlockCamera { Ip = secondCameraIp },
                    DoorDevice = new InterlockDoorDevice { Ip = doorIp },
                    DoorNos = effectiveDoorNos
                });
            }

            Options = new CameraAlarmDoorInterlockOptions
            {
                Enabled = enabled,
                WindowSeconds = windowSeconds,
                RestoreRetryIntervalMs = restoreRetryIntervalMs,
                Mappings = mappings
            };

            Resolver = new InterlockMappingResolver(Options, inner.Registry);
            WindowManager = new CameraAlarmWindowManager(Options.WindowSeconds);
            TargetManager = new DoorTargetStateManager();
            TaskFactory = new DoorControlTaskFactory(inner.Gateway);
            Service = new CameraDoorInterlockService(
                Options,
                Resolver,
                new AiopVideoPayloadParser(),
                WindowManager,
                TargetManager,
                TaskFactory,
                inner.Dispatcher,
                clock,
                scanIntervalMs: 15);
            Router = new AiopAlarmEventRouter(inner.Registry, Service, Options, Resolver);
            Router.Attach(inner.Gateway);
        }

        public int DoorDeviceId { get; }

        public string DoorIp { get; }

        public string CameraIp { get; }

        public string SecondCameraIp { get; }

        public MockHikvisionGateway Gateway => inner.Gateway;

        public CameraAlarmDoorInterlockOptions Options { get; }

        public InterlockMappingResolver Resolver { get; }

        public CameraAlarmWindowManager WindowManager { get; }

        public DoorTargetStateManager TargetManager { get; }

        public DoorControlTaskFactory TaskFactory { get; }

        public CameraDoorInterlockService Service { get; }

        public AiopAlarmEventRouter Router { get; }

        public void AddDoorDevice(int deviceId, string ip)
        {
            inner.AddRecord(deviceId, ip);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
            var login = inner.Lifecycle.SubmitLogin(deviceId, wait: true, requestId: "stage9-login-" + deviceId);
            Assert.True(login.Success, login.Message);
        }

        public void EmitAiopAlarm(string cameraIp, byte[] rawPayload = null, int userId = -1)
        {
            Gateway.EmitAlarm(new AlarmEventData
            {
                Command = AiopAlarmEventRouter.CommUploadAiopVideo,
                EventType = "COMM_UPLOAD_AIOP_VIDEO",
                DeviceIpAddress = cameraIp,
                UserId = userId,
                RawPayload = rawPayload ?? new byte[0]
            });
        }

        public int ControlGatewayCallCount()
        {
            return Gateway.Calls.Count(c => c.MethodName == "ControlGatewayAsync");
        }

        public void SpinForControlGatewayCalls(int minCount, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (ControlGatewayCallCount() >= minCount)
                {
                    return;
                }

                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            Router?.Dispose();
            Service?.Dispose();
            inner.Dispose();
        }
    }
}
