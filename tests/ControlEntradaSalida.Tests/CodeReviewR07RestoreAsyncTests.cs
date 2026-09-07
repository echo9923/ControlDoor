using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R07：窗口到期的恢复投递必须异步，慢设备不得阻塞报警调度循环；
    // 在途恢复不重复投递，迟到恢复结果按窗口代次失效。
    public static class CodeReviewR07RestoreAsyncTests
    {
        [TestCase]
        public static void CameraDoorInterlockService_SlowRestore_DoesNotBlockWindowExpiryLoop()
        {
            using (var fixture = new Stage9Fixture())
            {
                var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0);
                fixture.SpinForControlGatewayCalls(1);

                // 恢复指令阻塞设备工作线程 600ms：同步等待会拖住整个调度循环。
                fixture.Gateway.ConfigureResult("ControlGatewayAsync", request =>
                {
                    var gateRequest = request as GateControlRequest;
                    if (gateRequest != null && gateRequest.Command == GateControlCommand.Restore)
                    {
                        Thread.Sleep(600);
                    }

                    return new GateControlResponse
                    {
                        Success = true,
                        Code = "OK",
                        Message = "成功",
                        GateIndex = gateRequest == null ? 0 : gateRequest.GateIndex,
                        Command = gateRequest == null ? GateControlCommand.Restore : gateRequest.Command
                    };
                });

                var watch = Stopwatch.StartNew();
                fixture.Service.ExpireWindows(t0.AddSeconds(5));
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 300, "窗口到期处理被恢复任务阻塞：" + watch.ElapsedMilliseconds + "ms");

                // 恢复任务已开始执行并阻塞设备工作线程（网关调用在进入时即记录）。
                SpinUntil(() => fixture.Gateway.Calls.Count(c => CommandOf(c) == GateControlCommand.Restore) >= 1, "恢复指令未被异步执行。");

                // 恢复阻塞期间，新报警仍应立即建立窗口（不被同一次到期处理拖延）。
                fixture.EmitAiopAlarm(fixture.CameraIp);
                fixture.Service.ProcessEvents(t0.AddSeconds(6));
                Assert.Equal(1, fixture.WindowManager.GetActive().Count);
            }
        }

        [TestCase]
        public static void DoorTargetStateManager_InFlightRestore_PreventsDuplicateSubmission()
        {
            var manager = new DoorTargetStateManager();
            var target = new DoorTarget { DoorDeviceId = 1, DoorNo = 1, TargetKey = "1:1" };
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            manager.OnCameraWindowOpened("cam-a", target, now);
            manager.OnCameraWindowOpened("cam-b", target, now.AddSeconds(1));
            var firstExpiry = manager.OnCameraWindowClosed("cam-a", "1:1", now.AddSeconds(5));
            Assert.False(firstExpiry.ShouldSubmitRestore, "仍有活动摄像头时不应恢复。");

            var lastExpiry = manager.OnCameraWindowClosed("cam-b", "1:1", now.AddSeconds(7));
            Assert.True(lastExpiry.ShouldSubmitRestore);
            manager.MarkRestoreSubmitted("1:1", generation: 1, taskId: "t1", attempt: 0, now: now.AddSeconds(7));

            // 恢复在途：后续到期与到期重试扫描都不得重复投递。
            var duplicateExpiry = manager.OnCameraWindowClosed("cam-c", "1:1", now.AddSeconds(9));
            Assert.False(duplicateExpiry.ShouldSubmitRestore, "在途恢复期间不得重复投递。");
        }

        [TestCase]
        public static void DoorTargetStateManager_NewWindowGeneration_MarksOldRestoreStale()
        {
            var manager = new DoorTargetStateManager();
            var target = new DoorTarget { DoorDeviceId = 1, DoorNo = 1, TargetKey = "1:1" };
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            manager.OnCameraWindowOpened("cam-a", target, now);
            Assert.True(manager.TryGetActivity("1:1", out var firstWindow));
            var firstGeneration = firstWindow.Generation;

            manager.OnCameraWindowClosed("cam-a", "1:1", now.AddSeconds(5));
            manager.MarkRestoreSubmitted("1:1", generation: 1, taskId: "t1", attempt: 0, now: now.AddSeconds(5));

            // 恢复在途时新报警开启新窗口：代次递增，旧恢复结果据此失效。
            manager.OnCameraWindowOpened("cam-a", target, now.AddSeconds(6));
            Assert.True(manager.TryGetActivity("1:1", out var secondWindow));
            Assert.True(secondWindow.Generation > firstGeneration, "新窗口应递增代次。");
            Assert.False(secondWindow.RestoreInFlight, "新窗口应清除在途标记。");

            // 旧代次的迟到成功不清除新窗口的活动状态。
            Assert.True(manager.IsActive("1:1"));
        }

        private static GateControlCommand CommandOf(MockGatewayCall call)
        {
            if (call.MethodName != "ControlGatewayAsync")
            {
                return GateControlCommand.Open;
            }

            return ((GateControlRequest)call.Request).Command;
        }

        private static void SpinUntil(Func<bool> condition, string failureMessage)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }

            Assert.True(false, failureMessage);
        }
    }
}
