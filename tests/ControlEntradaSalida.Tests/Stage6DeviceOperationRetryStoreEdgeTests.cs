using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage6DeviceOperationRetryStoreEdgeTests
    {
        [TestCase]
        public static void DeviceOperationRetryStore_UpsertIntent_NullIntent_ReturnsInvalidArgument()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);

            var result = store.UpsertIntent(null);

            Assert.False(result.Success);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
            Assert.Equal(0, database.Commands.Count);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_UpsertIntent_MissingDevice_ReturnsInvalidArgument()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);

            var result = store.UpsertIntent(new DeviceOperationRetryIntent
            {
                DeviceId = 0,
                EmployeeId = "10001",
                Operation = "SyncPermission"
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
            Assert.Equal(0, database.Commands.Count);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_UpsertIntent_BlankEmployee_ReturnsInvalidArgument()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);

            var result = store.UpsertIntent(new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "   ",
                Operation = "SyncPermission"
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
            Assert.Equal(0, database.Commands.Count);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_UpsertIntent_UnsupportedOperation_ReturnsInvalidArgument()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);

            var result = store.UpsertIntent(new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "Bogus"
            });

            Assert.False(result.Success);
            Assert.Equal("INVALID_ARGUMENT", result.Code);
            Assert.Contains("不支持的补偿操作", result.Message);
            Assert.Equal(0, database.Commands.Count);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ApplyExecutionResult_NullResult_IsNoOp()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);

            store.ApplyExecutionResult(null, new DateTime(2026, 1, 1, 10, 0, 0));

            Assert.Equal(0, database.Commands.Count);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ApplyExecutionResult_AllSucceeded_MarksSuccessAndDeletes()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);
            var state = NewState();
            var result = new RetryExecutionResult(
                state,
                new[] { RetryOperation.Permission },
                null,
                true,
                "OK",
                "全部成功。",
                null);

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));

            Assert.True(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"));
            Assert.True(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ApplyExecutionResult_SameCompletionIsAppliedOnlyOnce()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);
            var state = NewState();
            var result = new RetryExecutionResult(
                state,
                new[] { RetryOperation.Permission },
                null,
                true,
                "OK",
                "全部成功。",
                null);

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));
            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 1));

            Assert.Equal(1, database.Commands.Count(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"));
            Assert.Equal(1, database.Commands.Count(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ApplyExecutionResult_WaitOutcome_IsNoOp()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);
            var result = new RetryExecutionResult(
                NewState(),
                new[] { RetryOperation.Permission },
                null,
                true,
                "TIMEOUT",
                "等待层超时，最终设备任务仍在运行。",
                null)
            {
                FinalDeviceTaskResult = new ControlDoor.Devices.Tasks.DeviceTaskResult
                {
                    Code = "TIMEOUT",
                    IsWaitOutcome = true
                }
            };

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));

            Assert.Equal(0, database.Commands.Count);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ApplyExecutionResult_NonRetryableTerminalCode_MarksTerminal()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);
            var state = NewState();
            var result = new RetryExecutionResult(
                state,
                Enumerable.Empty<RetryOperation>(),
                RetryOperation.Face,
                false,
                "DEVICE_UNSUPPORTED",
                "设备不支持该功能。",
                23);

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));

            var terminal = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure");
            Assert.Contains("@lastError=DEVICE_UNSUPPORTED", terminal.CommandText);
            Assert.False(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry"));
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ApplyExecutionResult_RetryableAtMaxAttempts_MarksExhausted()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { MaxRetryAttempts = 10 });
            var state = NewState(attemptCount: 9);
            var result = new RetryExecutionResult(
                state,
                Enumerable.Empty<RetryOperation>(),
                RetryOperation.Face,
                true,
                "SDK_ERROR",
                "可重试错误。",
                7);

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));

            var terminal = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure");
            Assert.Contains("@lastError=RETRY_EXHAUSTED", terminal.CommandText);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_ScheduleRetry_BumpsAttemptAndGuardsExhausted()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 5,
                MaxRetryDelaySeconds = 30
            });

            store.ScheduleRetry(NewState(attemptCount: 0), "SDK_ERROR", "可重试错误。", new DateTime(2026, 1, 1, 10, 0, 0));

            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry");
            Assert.Contains("attempt_count = @attemptCount", command.CommandText);
            Assert.Contains("@attemptCount=1", command.CommandText);
            Assert.Contains("AND exhausted_at IS NULL", command.CommandText);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_DeferOffline_DoesNotBumpAttempt()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 5
            });

            store.DeferOffline(NewState(), "DEVICE_OFFLINE", "设备离线。", new DateTime(2026, 1, 1, 10, 0, 0));

            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.DeferOffline");
            Assert.Contains("next_retry_at = @nextRetryAt", command.CommandText);
            Assert.False(command.CommandText.Contains("attempt_count"));
        }

        [TestCase]
        public static void DeviceOperationRetryStore_TerminalError_IsTruncatedToMaxLength()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { MaxRetryAttempts = 10 });
            var state = NewState(attemptCount: 9);
            var result = new RetryExecutionResult(
                state,
                Enumerable.Empty<RetryOperation>(),
                RetryOperation.Face,
                true,
                "SDK_ERROR",
                new string('x', 3000),
                7);

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));

            var terminal = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure");
            var marker = "@lastError=";
            var value = terminal.CommandText.Substring(terminal.CommandText.IndexOf(marker) + marker.Length);
            Assert.Equal(2000, value.Length);
        }

        private static DeviceOperationRetryState NewState(long id = 1, int attemptCount = 0)
        {
            return new DeviceOperationRetryState
            {
                Id = id,
                DeviceId = 1,
                EmployeeId = "10001",
                AttemptCount = attemptCount
            };
        }
    }
}
