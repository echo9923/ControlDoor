using System;
using System.Collections.Generic;
using System.Linq;
using ControlDoor.CameraDoorInterlock;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9DoorTargetStateManagerTests
    {
        [TestCase]
        public static void Stage9Target_FirstCameraOpen_TriggersAlwaysClose()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);

            var change = manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);

            Assert.True(change.ShouldSubmitAlwaysClose);
            Assert.False(change.ShouldSubmitRestore);
        }

        [TestCase]
        public static void Stage9Target_SecondCameraOnSharedDoor_DoesNotRepeatAlwaysClose()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);

            var second = manager.OnCameraWindowOpened("cam-2", Target("10:1"), now.AddSeconds(1));

            Assert.False(second.ShouldSubmitAlwaysClose);
        }

        [TestCase]
        public static void Stage9Target_CameraCloseWithOthersActive_DoesNotRestore()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);
            manager.OnCameraWindowOpened("cam-2", Target("10:1"), now);

            var close = manager.OnCameraWindowClosed("cam-1", "10:1", now.AddSeconds(5));

            Assert.False(close.ShouldSubmitRestore);
        }

        [TestCase]
        public static void Stage9Target_LastCameraClose_TriggersRestore()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);
            manager.OnCameraWindowOpened("cam-2", Target("10:1"), now);
            manager.OnCameraWindowClosed("cam-1", "10:1", now.AddSeconds(5));

            var lastClose = manager.OnCameraWindowClosed("cam-2", "10:1", now.AddSeconds(6));

            Assert.True(lastClose.ShouldSubmitRestore);
            Assert.NotNull(lastClose.Activity);
            Assert.Equal(10, lastClose.Activity.DoorDeviceId);
            Assert.Equal(1, lastClose.Activity.DoorNo);
        }

        [TestCase]
        public static void Stage9Target_RestoreSuccess_RemovesActivityWhenEmpty()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);
            manager.OnCameraWindowClosed("cam-1", "10:1", now.AddSeconds(5));

            manager.MarkRestoreSucceeded("10:1", now.AddSeconds(6));

            Assert.False(manager.TryGetActivity("10:1", out var activity));
        }

        [TestCase]
        public static void Stage9Target_RestoreFailure_SchedulesRetry()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);
            manager.OnCameraWindowClosed("cam-1", "10:1", now.AddSeconds(5));

            manager.RecordRestoreFailure("10:1", 1, now.AddSeconds(1), now);

            var due = manager.GetDueRestoreRetries(now.AddSeconds(1));
            Assert.Equal(1, due.Count);
        }

        [TestCase]
        public static void Stage9Target_TerminalFailure_NotScheduledForAutoRetry()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);
            manager.OnCameraWindowClosed("cam-1", "10:1", now.AddSeconds(5));

            manager.RecordRestoreFailure("10:1", 3, null, now);

            Assert.Equal(0, manager.GetDueRestoreRetries(now.AddDays(1)).Count);
            Assert.Equal(1, manager.GetOutstandingTargets().Count);
        }

        [TestCase]
        public static void Stage9Target_OutstandingTargets_IncludesActiveAndPending()
        {
            var manager = new DoorTargetStateManager();
            var now = new DateTime(2026, 1, 1, 8, 0, 0);
            manager.OnCameraWindowOpened("cam-1", Target("10:1"), now);
            manager.OnCameraWindowOpened("cam-2", Target("20:1"), now);
            manager.OnCameraWindowClosed("cam-2", "20:1", now.AddSeconds(5));
            manager.RecordRestoreFailure("20:1", 1, now.AddSeconds(10), now.AddSeconds(5));

            var outstanding = manager.GetOutstandingTargets();

            Assert.Equal(2, outstanding.Count);
            Assert.True(outstanding.Any(t => t.TargetKey == "10:1" && t.IsActive));
            Assert.True(outstanding.Any(t => t.TargetKey == "20:1"));
        }

        private static DoorTarget Target(string targetKey)
        {
            var parts = targetKey.Split(':');
            return new DoorTarget
            {
                TargetKey = targetKey,
                DoorDeviceId = int.Parse(parts[0]),
                DoorNo = int.Parse(parts[1])
            };
        }
    }
}
