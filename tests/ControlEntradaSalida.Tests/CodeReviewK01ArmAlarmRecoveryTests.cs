using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;
using ControlDoor.Hikvision;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 K1：布防任务在共享工作通道排队超过执行期限或入队被拒后，
    // 设备在线却永久缺失布防句柄；必须无需再次掉线即可自动恢复布防。
    public static class CodeReviewK01ArmAlarmRecoveryTests
    {
        [TestCase]
        public static void DeviceLifecycle_ArmAlarmExpiredInQueue_ReArmsWithoutReconnect()
        {
            using (var fixture = new Stage4Fixture(defaultTaskTimeoutMilliseconds: 200))
            {
                fixture.Options.ReArmBaseDelayMs = 10;
                fixture.Options.ReArmMaxDelayMs = 50;
                // 设备 1 与 3 路由到同一共享工作线程（1 % 2 == 3 % 2），模拟共享通道繁忙。
                fixture.AddRecord(1);
                fixture.AddRecord(3, ipAddress: "192.168.1.66");
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("k01-test", CancellationToken.None, null)).GetAwaiter().GetResult();

                // 两个长阻塞任务先后占用共享通道：登录（High）在第一个阻塞任务之后插队执行，
                // 登录委托末尾投递的布防任务（Normal）排在第二个阻塞任务之后，
                // 在队列中等待超过 200ms 默认期限后被判过期（ExpiredBeforeExecution）。
                var firstStarted = new ManualResetEventSlim(false);
                fixture.Dispatcher.Submit(Blocker(3, 400, firstStarted, DeviceTaskPriority.Normal));
                Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(2)), "第一个阻塞任务未开始执行。");
                fixture.Dispatcher.Submit(Blocker(3, 400, null, DeviceTaskPriority.Normal));
                var login = fixture.Lifecycle.SubmitLogin(1, wait: false, requestId: "k01-login");
                Assert.True(login.Success, "登录任务投递失败: " + login.Message);

                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync") >= 1, "排队过期后没有自动重新布防。");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "重新布防后本地布防句柄未恢复。");

                // 核心断言：无需再次掉线重连（登录只发生一次）即可恢复布防。
                var loginCount = fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync");
                Assert.Equal(1, loginCount);
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ArmAlarmQueueFullRejected_SchedulesReArmUntilChannelFrees()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
            using (var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 1, queueCapacityPerWorker: 1, defaultTaskTimeoutMilliseconds: 5000))
            using (var scheduler = new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions { WakeupMaxSleepMilliseconds = 10 }))
            using (var gateway = new MockHikvisionGateway())
            {
                var repository = new InMemoryDeviceRepository();
                repository.Add(new DeviceRecord
                {
                    DeviceId = 1,
                    DeviceName = "门禁-1",
                    IpAddress = "192.168.1.64",
                    Port = 8000,
                    Username = "admin",
                    Password = "12345",
                    Enabled = true,
                    Types = new System.Collections.Generic.List<DeviceType> { DeviceType.Acs }
                });
                var options = new DeviceLifecycleOptions
                {
                    LoginTimeoutMs = 5000,
                    HealthCheckIntervalMs = 1000,
                    ReconnectBaseDelayMs = 10,
                    ReconnectMaxDelayMs = 100,
                    ReArmBaseDelayMs = 10,
                    ReArmMaxDelayMs = 50,
                    AlarmEnabled = true
                };
                using (var lifecycle = new DeviceLifecycleService(registry, dispatcher, scheduler, repository, gateway, options, null))
                {
                    lifecycle.LoadEnabledDevices(enqueueLogin: false);
                    scheduler.StartAsync(new BackgroundTaskContext("k01-qfull", CancellationToken.None, null)).GetAwaiter().GetResult();

                    var login = lifecycle.SubmitLogin(1, wait: true, requestId: "k01-qfull-login");
                    Assert.True(login.Success, "登录失败: " + login.Message);
                    WaitUntil(() => registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "登录后未布防。");
                    var armedCount = gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync");
                    Assert.Equal(1, armedCount);

                    // 模拟布防句柄丢失后，在通道被占满时请求重布防：直接投递被 QUEUE_FULL 拒绝，
                    // 观察器应补排延迟重布防并随通道空闲最终恢复布防。
                    registry.ClearAlarmHandle(1, DateTime.Now);
                    var blockerStarted = new ManualResetEventSlim(false);
                    dispatcher.Submit(Blocker(1, 200, blockerStarted, DeviceTaskPriority.Normal));
                    Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)), "阻塞任务未开始执行。");
                    dispatcher.Submit(Blocker(1, 50, null, DeviceTaskPriority.Normal));
                    var submitted = lifecycle.SubmitArmAlarm(1, wait: false, requestId: "k01-qfull-arm");
                    Assert.False(submitted.Success, "队列已满时投递应被拒绝。");
                    Assert.Equal("QUEUE_FULL", submitted.Code);

                    WaitUntil(() => registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "队列满被拒后没有恢复布防。");
                    Assert.Equal(2, gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
                }
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ArmAlarmObserver_DoesNotReArmAfterManualDisarm()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReArmBaseDelayMs = 10;
                fixture.Options.ReArmMaxDelayMs = 50;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("k01-disarm", CancellationToken.None, null)).GetAwaiter().GetResult();

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "k01-disarm-login");
                Assert.True(login.Success, "登录失败: " + login.Message);
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "登录后未布防。");
                var disarm = fixture.Lifecycle.DisarmDeviceAlarm(1, requestId: "k01-disarm");
                Assert.True(disarm.Success, "手动撤防失败: " + disarm.Message);
                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "CloseAlarmAsync") >= 1, "撤防没有关闭布防句柄。");

                // 手动撤防后补排布防必须被 SUPERSEDED 吞掉：不调用 SDK，也不留下延迟重布防任务。
                var submitted = fixture.Lifecycle.SubmitArmAlarm(1, wait: false, requestId: "k01-disarm-arm");
                Assert.True(submitted.Success, "布防任务投递失败: " + submitted.Message);
                Thread.Sleep(200);
                Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4ReArm"));
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ArmAlarmObserver_IdempotentWhenHandleAlreadyPresent()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "k01-idem-login");
                Assert.True(login.Success, "登录失败: " + login.Message);
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "登录后未布防。");

                var submitted = fixture.Lifecycle.SubmitArmAlarm(1, wait: false, requestId: "k01-idem-arm");
                Assert.True(submitted.Success, "布防任务投递失败: " + submitted.Message);
                Thread.Sleep(200);
                Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
            }
        }

        [TestCase]
        public static void DeviceHealthCheck_MissingAlarmHandle_SelfHealsEvenWhenProbeDisabled()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.AlarmStatusProbeEnabled = false;
                fixture.Options.ReArmBaseDelayMs = 10;
                fixture.Options.ReArmMaxDelayMs = 50;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("k01-heal", CancellationToken.None, null)).GetAwaiter().GetResult();

                var login = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "k01-heal-login");
                Assert.True(login.Success, "登录失败: " + login.Message);
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "登录后未布防。");

                // 模拟本地布防句柄丢失：即使 AlarmStatusProbeEnabled=false，健康检查也必须补排重布防。
                fixture.Registry.ClearAlarmHandle(1, DateTime.Now);
                var check = fixture.Lifecycle.SubmitHealthCheck(1, wait: true, requestId: "k01-heal-check");
                Assert.True(check.Success, "健康检查失败: " + check.Message);

                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "探测关闭时健康检查没有补排重布防。");
                Assert.Equal(2, fixture.Gateway.Calls.Count(call => call.MethodName == "SetAlarmAsync"));
                // 开关关闭时不得向设备侧主动探测布防状态。
                Assert.Equal(0, fixture.Gateway.Calls.Count(call => call.MethodName == "GetAlarmDeploymentStatusAsync"));
            }
        }

        private static DeviceSdkTask Blocker(int deviceId, int milliseconds, ManualResetEventSlim started, DeviceTaskPriority priority)
        {
            var task = new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "K01Blocker", context => Task.Run(() =>
            {
                started?.Set();
                Thread.Sleep(milliseconds);
                return Ok(context.Task);
            }));
            task.Priority = priority;
            task.TimeoutMilliseconds = 10000;
            return task;
        }

        private static DeviceTaskResult Ok(DeviceSdkTask task)
        {
            var now = DateTime.Now;
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
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
    }
}
