using System;
using System.Threading;
using System.Threading.Tasks;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Tasks;
using ControlDoor.Devices.Workers;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R4：共享 SDK 通道的排队等待与积压可观测——queueWaitMs 计量、
    // 慢排队告警判定（低优先级健康检查豁免）、周期统计快照字段。
    public static class CodeReviewR04WorkerObservabilityTests
    {
        [TestCase]
        public static void DeviceSdkWorker_CalculateQueueWaitMilliseconds_UsesStartOrCompletionTime()
        {
            var now = DateTime.Now;
            var task = new DeviceSdkTask(1, DeviceTaskType.Login, "r04", context => Task.FromResult(Ok(context.Task)));
            // 未入队：无法计量。
            Assert.Equal(null, DeviceSdkWorker.CalculateQueueWaitMilliseconds(task, null));

            task.MarkQueued(now, 1, 30000);
            task.MarkRunning(now.AddSeconds(2));
            var result = DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now.AddSeconds(3), now.AddSeconds(3));
            // 以开始执行时刻计算排队等待。
            Assert.Equal(2000, DeviceSdkWorker.CalculateQueueWaitMilliseconds(task, result));

            // 执行前过期（从未 MarkRunning）：以完成（拒绝）时刻计算。
            var expired = new DeviceSdkTask(1, DeviceTaskType.Login, "r04-expired", context => Task.FromResult(Ok(context.Task)));
            expired.MarkQueued(now, 1, 30000);
            var timeout = DeviceTaskResult.FromTask(expired, false, "TIMEOUT", "Task expired before execution.", DeviceConnectionStatus.Online, now.AddSeconds(4), now.AddSeconds(4));
            timeout.ExpiredBeforeExecution = true;
            Assert.Equal(4000, DeviceSdkWorker.CalculateQueueWaitMilliseconds(expired, timeout));
        }

        [TestCase]
        public static void DeviceSdkWorker_ShouldWarnSlowQueueWait_ExcludesLowPriorityTasks()
        {
            var normal = new DeviceSdkTask(1, DeviceTaskType.Login, "r04-normal", context => Task.FromResult(Ok(context.Task)));
            var healthCheck = new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "r04-health", context => Task.FromResult(Ok(context.Task)));
            healthCheck.Priority = DeviceTaskPriority.Low;

            Assert.False(DeviceSdkWorker.ShouldWarnSlowQueueWait(normal, 4000, 5000), "未超阈值不应告警。");
            Assert.True(DeviceSdkWorker.ShouldWarnSlowQueueWait(normal, 6000, 5000), "超过阈值应告警。");
            Assert.False(DeviceSdkWorker.ShouldWarnSlowQueueWait(healthCheck, 6000, 5000), "低优先级健康检查按设计长时间等待，不告警。");
            Assert.False(DeviceSdkWorker.ShouldWarnSlowQueueWait(null, 6000, 5000), "空任务不告警。");
        }

        [TestCase]
        public static void DeviceSdkWorker_SnapshotExposesBacklogForStats()
        {
            var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
            using (var worker = new DeviceSdkWorker(0, 50, 5000, registry, null, slowQueueWaitWarningMs: 5000, statsLogIntervalSeconds: 60))
            {
                worker.Start();
                Register(registry, 1);

                var blockerStarted = new ManualResetEventSlim(false);
                var submitted = worker.Enqueue(Blocker(1, 200, blockerStarted));
                Assert.True(submitted.Accepted);
                Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)), "阻塞任务未开始执行。");

                var queued = worker.Enqueue(new DeviceSdkTask(1, DeviceTaskType.HealthCheck, "r04-queued", context => Task.FromResult(Ok(context.Task))));
                Assert.True(queued.Accepted);

                var snapshot = worker.GetSnapshot();
                Assert.Equal(1, snapshot.QueueLength);
                Assert.NotNull(snapshot.OldestQueuedTaskAgeMilliseconds);
                Assert.True(snapshot.CompletedTaskCount >= 0 && snapshot.FailedTaskCount >= 0);
            }
        }

        private static DeviceTaskResult Ok(DeviceSdkTask task)
        {
            var now = DateTime.Now;
            return DeviceTaskResult.FromTask(task, true, "OK", "ok", DeviceConnectionStatus.Online, now, now);
        }

        private static DeviceSdkTask Blocker(int deviceId, int milliseconds, ManualResetEventSlim started)
        {
            return new DeviceSdkTask(deviceId, DeviceTaskType.HealthCheck, "R04Blocker", context => Task.Run(() =>
            {
                started.Set();
                Thread.Sleep(milliseconds);
                return Ok(context.Task);
            }));
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
    }
}
