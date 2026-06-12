using System;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    internal sealed class Stage4Fixture : IDisposable
    {
        public Stage4Fixture()
        {
            Registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
            Dispatcher = new DeviceSdkDispatcher(Registry, workerCount: 2, queueCapacityPerWorker: 50, defaultTaskTimeoutMilliseconds: 5000);
            DelayedScheduler = new DelayedDeviceTaskScheduler(Dispatcher, new DelayedDeviceTaskSchedulerOptions { WakeupMaxSleepMilliseconds = 10 });
            Gateway = new MockHikvisionGateway();
            Repository = new InMemoryDeviceRepository();
            Options = new DeviceLifecycleOptions
            {
                LoginTimeoutMs = 5000,
                LogoutTimeoutMs = 5000,
                HealthCheckIntervalMs = 1000,
                HealthCheckTimeoutMs = 5000,
                ReconnectBaseDelayMs = 10,
                ReconnectMaxDelayMs = 100,
                MaxReconnectAttempts = 3,
                FailureThreshold = 3,
                AlarmEnabled = true
            };
            Lifecycle = new DeviceLifecycleService(Registry, Dispatcher, DelayedScheduler, Repository, Gateway, Options);
        }

        public DeviceRuntimeRegistry Registry { get; }

        public DeviceSdkDispatcher Dispatcher { get; }

        public DelayedDeviceTaskScheduler DelayedScheduler { get; }

        public MockHikvisionGateway Gateway { get; }

        public InMemoryDeviceRepository Repository { get; }

        public DeviceLifecycleOptions Options { get; }

        public DeviceLifecycleService Lifecycle { get; }

        public void AddRecord(int deviceId = 1, string ipAddress = "192.168.1.64", bool enabled = true, string password = "12345")
        {
            Repository.Add(new DeviceRecord
            {
                DeviceId = deviceId,
                DeviceName = "门禁-" + deviceId,
                Description = "测试设备",
                IpAddress = ipAddress,
                Port = 8000,
                Username = "admin",
                Password = password,
                Enabled = enabled
            });
        }

        public void Dispose()
        {
            Dispatcher.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Gateway.Dispose();
        }
    }
}
