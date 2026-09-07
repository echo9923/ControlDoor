using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R08：前台同步必须按设备工作线程分道并行——
    // 慢设备所在道阻塞时，其他道照常执行；同一道（同设备）仍串行。
    public static class CodeReviewR08LaneParallelTests
    {
        [TestCase]
        public static void SyncPermissions_SlowDevice_DoesNotSerializeOtherWorkerDevices()
        {
            using (var fixture = new Stage5Fixture())
            {
                // Stage4Fixture WorkerCount=2：设备1/3 在道1，设备2 在道0。
                fixture.AddOnlineDevice(deviceId: 1);
                fixture.AddOnlineDevice(deviceId: 2);
                fixture.AddOnlineDevice(deviceId: 3);

                var gate = new object();
                var startByUser = new Dictionary<int, long>();
                var endByUser = new Dictionary<int, long>();
                fixture.Gateway.ConfigureResult("UpsertPersonAsync", (Func<object, int>)(request =>
                {
                    var upsert = (UpsertPersonRequest)request;
                    var began = StopwatchTimestamp();
                    if (upsert.UserId == 1)
                    {
                        // 设备1 慢 500ms，阻塞其所在工作线程。
                        Thread.Sleep(500);
                    }

                    var ended = StopwatchTimestamp();
                    lock (gate)
                    {
                        startByUser[upsert.UserId] = began;
                        endByUser[upsert.UserId] = ended;
                    }

                    return 0;
                }));

                var response = fixture.Response(fixture.Service.SyncPermissions(
                    @"{""items"":[{""employee_id"":""10001"",""name"":""张三"",""permission_code"":2}]}",
                    fixture.Context("r08-parallel")));

                Assert.Equal("OK", response["code"]);
                Assert.Equal(3, startByUser.Count);

                // 不同道（设备2）与慢设备（设备1）并行：设备2 在设备1 结束前已完成。
                Assert.True(endByUser[2] < endByUser[1], "另一工作线程上的设备被慢设备串行拖延。");
                Assert.True(startByUser[2] < endByUser[1], "不同道的设备未并行执行。");

                // 同一道（设备3 与设备1 同在道1）：设备3 必须等设备1 结束后才开始。
                Assert.True(startByUser[3] >= endByUser[1], "同一工作线程上的设备被并行调用。");
            }
        }

        private static long StopwatchTimestamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }
}
