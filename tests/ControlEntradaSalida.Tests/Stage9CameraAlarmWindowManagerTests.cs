using System;
using System.Collections.Generic;
using ControlDoor.CameraDoorInterlock;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9CameraAlarmWindowManagerTests
    {
        [TestCase]
        public static void Stage9Window_FirstAlarm_OpensWindowEndingAtNowPlusWindowSeconds()
        {
            var manager = new CameraAlarmWindowManager(5);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            var result = manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, now);

            Assert.True(result.OpenedNew);
            Assert.Equal(now.AddSeconds(5), result.Window.EndsAt);
            Assert.Equal(1, result.Window.TriggeredCount);
        }

        [TestCase]
        public static void Stage9Window_RepeatAlarmWithinWindow_DoesNotRefreshEndsAt()
        {
            var manager = new CameraAlarmWindowManager(5);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, t0);

            var second = manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, t0.AddSeconds(2));

            Assert.False(second.OpenedNew);
            Assert.Equal(t0.AddSeconds(5), second.Window.EndsAt);
            Assert.Equal(2, second.Window.TriggeredCount);
        }

        [TestCase]
        public static void Stage9Window_AlarmAfterExpiry_OpensNewWindow()
        {
            var manager = new CameraAlarmWindowManager(5);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, t0);
            manager.ExpireDue(t0.AddSeconds(5));

            var second = manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, t0.AddSeconds(6));

            Assert.True(second.OpenedNew);
            Assert.Equal(t0.AddSeconds(6).AddSeconds(5), second.Window.EndsAt);
        }

        [TestCase]
        public static void Stage9Window_ExpireDue_RemovesOnlyDueWindows()
        {
            var manager = new CameraAlarmWindowManager(5);
            var t0 = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, t0);
            manager.OpenOrRecord("cam-2", new List<string> { "10:1" }, t0.AddSeconds(3));

            var expired = manager.ExpireDue(t0.AddSeconds(5));

            Assert.Equal(1, expired.Count);
            Assert.Equal("cam-1", expired[0].CameraKey);
            Assert.True(manager.HasActiveWindow("cam-2"));
            Assert.False(manager.HasActiveWindow("cam-1"));
        }

        [TestCase]
        public static void Stage9Window_GetActive_ReturnsAllOpenWindows()
        {
            var manager = new CameraAlarmWindowManager(5);
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OpenOrRecord("cam-1", new List<string> { "10:1" }, now);
            manager.OpenOrRecord("cam-2", new List<string> { "10:1" }, now);

            Assert.Equal(2, manager.GetActive().Count);
        }

        [TestCase]
        public static void Stage9Window_WindowSecondsClampedToMinimumOne()
        {
            var manager = new CameraAlarmWindowManager(0);
            Assert.Equal(1, manager.WindowSeconds);
        }
    }
}
