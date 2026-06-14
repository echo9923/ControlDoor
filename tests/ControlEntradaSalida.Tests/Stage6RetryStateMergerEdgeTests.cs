using System;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage6RetryStateMergerEdgeTests
    {
        [TestCase]
        public static void RetryStateMerger_NullIntent_Throws()
        {
            var merger = new RetryStateMerger();

            Stage3TestReflection.Expect<ArgumentNullException>(() =>
                merger.Merge(null, null, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true));
        }

        [TestCase]
        public static void RetryStateMerger_UnparseableOperation_Throws()
        {
            var merger = new RetryStateMerger();

            Stage3TestReflection.Expect<ArgumentException>(() =>
                merger.Merge(null, new DeviceOperationRetryIntent
                {
                    DeviceId = 1,
                    EmployeeId = "10001",
                    Operation = "Bogus"
                }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true));
        }

        [TestCase]
        public static void RetryStateMerger_NoExistingState_CreatesInsert()
        {
            var merger = new RetryStateMerger();

            var result = merger.Merge(null, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "  10001  ",
                Operation = "SyncPermission",
                PermissionLevel = 7,
                CreatedAt = new DateTime(2026, 1, 1)
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.True(result.Insert);
            Assert.False(result.ConflictReset);
            Assert.False(result.ReactivatedTerminal);
            Assert.Equal(1, result.State.DeviceId);
            Assert.Equal("10001", result.State.EmployeeId);
            Assert.True(result.State.PermissionPending);
            Assert.Equal(7, result.State.PermissionLevel);
        }

        [TestCase]
        public static void RetryStateMerger_SameKindUpdate_PreservesAttemptCount()
        {
            var merger = new RetryStateMerger();
            var existing = new DeviceOperationRetryState
            {
                Id = 1,
                DeviceId = 1,
                EmployeeId = "10001",
                PermissionPending = true,
                PermissionLevel = 3,
                AttemptCount = 5
            };

            var result = merger.Merge(existing, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "SyncPermission",
                PermissionLevel = 7
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.True(result.SameKindUpdate);
            Assert.False(result.ConflictReset);
            Assert.Equal(5, result.State.AttemptCount);
            Assert.True(result.State.PermissionPending);
            Assert.Equal(7, result.State.PermissionLevel);
        }

        [TestCase]
        public static void RetryStateMerger_LastError_PrefersReasonCodeAndMessage()
        {
            var merger = new RetryStateMerger();

            var result = merger.Merge(null, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "SyncPermission",
                ReasonCode = "DEVICE_OFFLINE",
                ReasonMessage = "设备离线",
                LastError = "legacy error"
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.Equal("DEVICE_OFFLINE: 设备离线", result.State.LastError);
        }

        [TestCase]
        public static void RetryStateMerger_LastError_FallsBackToTrimmedLastError()
        {
            var merger = new RetryStateMerger();

            var result = merger.Merge(null, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "SyncPermission",
                LastError = "   legacy error   "
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.Equal("legacy error", result.State.LastError);
        }

        [TestCase]
        public static void RetryStateMerger_ExplicitNextRetryAt_IsHonored()
        {
            var merger = new RetryStateMerger();
            var scheduled = new DateTime(2026, 1, 9, 12, 0, 0);

            var result = merger.Merge(null, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "SyncPermission",
                NextRetryAt = scheduled
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.Equal(scheduled, result.State.NextRetryAt);
        }
    }
}
