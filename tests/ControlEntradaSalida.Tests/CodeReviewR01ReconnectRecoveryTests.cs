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
    // 代码复核 R01：重连任务在设备队列中排队超过执行期限后，必须重新安排重连，
    // 设备不能停留在 ReconnectPending 且无人补救。
    public static class CodeReviewR01ReconnectRecoveryTests
    {
        [TestCase]
        public static void DelayedDeviceTaskScheduler_CompletionObserver_ReceivesExpiredBeforeExecutionResult()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
            Register(registry, 1);
            using (var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 50, defaultTaskTimeoutMilliseconds: 5000))
            using (var scheduler = new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions { WakeupMaxSleepMilliseconds = 10 }))
            {
                var now = DateTime.Now;
                // 阻塞任务必须已开始执行再派发高优先级登录任务：设备队列是优先级队列，
                // 阻塞任务未出队时高优先级任务会插队执行，破坏"排队过期"前提。
                var blockerStarted = new ManualResetEventSlim(false);
                dispatcher.Submit(Blocker(1, 400, blockerStarted));
                Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)), "阻塞任务未开始执行。");

                DeviceTaskResult observed = null;
                var observedSignal = new ManualResetEventSlim(false);
                var delayed = new DelayedDeviceTask(
                    1,
                    DeviceTaskType.Login,
                    DeviceTaskPriority.High,
                    now,
                    "r01:observer",
                    "R01Test",
                    () =>
                    {
                        var task = new DeviceSdkTask(1, DeviceTaskType.Login, "R01Login", context => Task.FromResult(Ok(context.Task)));
                        task.TimeoutMilliseconds = 60;
                        return task;
                    },
                    now);
                delayed.CompletionObserver = (task, result) =>
                {
                    observed = result;
                    observedSignal.Set();
                };

                scheduler.Schedule(delayed);
                scheduler.DispatchDueTasks(DateTime.Now);

                Assert.True(observedSignal.Wait(TimeSpan.FromSeconds(5)), "完成观察回调没有被调用。");
                Assert.NotNull(observed);
                Assert.False(observed.Success);
                Assert.Equal("TIMEOUT", observed.Code);
                Assert.True(observed.ExpiredBeforeExecution, "排队过期应设置 ExpiredBeforeExecution 标记。");
            }
        }

        [TestCase]
        public static void DeviceLifecycle_ReconnectTaskExpiredInQueue_ReschedulesAndLogsInAgain()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.LoginTimeoutMs = 150;
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 20;
                fixture.Options.MaxReconnectAttempts = 0;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                // 第一次登录失败：设备进入 ReconnectPending，重连延迟任务入队。
                fixture.Gateway.ConfigureException("LoginAsync", new DeviceGatewayException("Login", SdkError.FromCode(7)));
                var first = fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r01-first");
                var afterFailure = fixture.Registry.TryGetByDeviceId(1).Snapshot;

                Assert.False(first.Success);
                Assert.Equal(DeviceConnectionStatus.ReconnectPending, afterFailure.Status);
                Assert.True(fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect") >= 1);

                // 后续登录恢复成功（空行为回落到 mock 真实逻辑以注册会话），然后阻塞设备工作线程，
                // 使重连登录任务在队列中超过执行期限。
                fixture.Gateway.Configure("LoginAsync", new MockGatewayBehavior());
                var blockerStarted = new ManualResetEventSlim(false);
                fixture.Dispatcher.Submit(Blocker(1, 400, blockerStarted));
                Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)), "阻塞任务未开始执行。");
                fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("r01-test", CancellationToken.None, null)).GetAwaiter().GetResult();

                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") >= 2, "排队过期后没有重新登录。");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "重连后设备未恢复在线。");

                var snapshot = fixture.Registry.TryGetByDeviceId(1).Snapshot;
                Assert.True(snapshot.SdkUserId.HasValue);
            }
        }

        [TestCase]
        public static void DeviceHealthCheckBackgroundTask_SelfHealsStuckReconnectPending()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.Options.ReconnectBaseDelayMs = 10;
                fixture.Options.ReconnectMaxDelayMs = 20;
                fixture.Options.MaxReconnectAttempts = 0;
                fixture.Options.ReconnectSelfHealGraceMs = 0;
                fixture.Options.HealthCheckIntervalMs = 50;
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);

                fixture.Gateway.ConfigureException("LoginAsync", new DeviceGatewayException("Login", SdkError.FromCode(7)));
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "r01-heal-fail");
                Assert.Equal(DeviceConnectionStatus.ReconnectPending, fixture.Registry.TryGetByDeviceId(1).Snapshot.Status);

                // 模拟重连安排丢失：取消已调度的重连任务，NextReconnectAt 逾期后由健康检查自愈补排。
                fixture.DelayedScheduler.CancelByTaskKey("stage4:reconnect:1", "r01-test-simulate-loss");
                Assert.Equal(0, fixture.DelayedScheduler.GetSnapshot().GetSourceCount("Stage4Reconnect"));
                Thread.Sleep(30);

                fixture.Gateway.Configure("LoginAsync", new MockGatewayBehavior());
                var healthTask = new DeviceHealthCheckBackgroundTask(fixture.Lifecycle, fixture.Options);
                healthTask.StartAsync(new BackgroundTaskContext("r01-heal", CancellationToken.None, null)).GetAwaiter().GetResult();
                fixture.DelayedScheduler.StartAsync(new BackgroundTaskContext("r01-heal", CancellationToken.None, null)).GetAwaiter().GetResult();

                WaitUntil(() => fixture.Gateway.Calls.Count(call => call.MethodName == "LoginAsync") >= 2, "健康检查自愈没有重新登录。");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.Status == DeviceConnectionStatus.Online, "自愈重连后设备未恢复在线。");

                healthTask.StopAsync(new BackgroundTaskContext("r01-heal-stop", CancellationToken.None, null)).GetAwaiter().GetResult();
            }
        }

        private static DeviceSdkTask Blocker(int deviceId, int milliseconds, ManualResetEventSlim started = null)
        {
            return new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "R01Blocker", context => Task.Run(() =>
            {
                started?.Set();
                Thread.Sleep(milliseconds);
                return Ok(context.Task);
            }));
        }

        private static DeviceTaskResult Ok(DeviceSdkTask task)
        {
            var now = DateTime.Now;
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
        }

        private static void Register(DeviceRuntimeRegistry registry, int deviceId)
        {
            var result = registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = deviceId,
                DeviceName = "device-" + deviceId,
                IpAddress = "10.2.8." + deviceId,
                Port = 8000,
                Username = "admin",
                Password = "pwd",
                Enabled = true,
                CreatedAt = DateTime.Now
            });
            Assert.True(result.Success, result.Message);
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
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
