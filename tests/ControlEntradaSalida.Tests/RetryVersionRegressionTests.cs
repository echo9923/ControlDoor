using System;
using System.Linq;
using ControlDoor.Database;
using ControlDoor.Devices.Tasks;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class RetryVersionRegressionTests
    {
        [TestCase]
        public static void RetryStore_AllStateTransitions_CompareIntentVersion()
        {
            var database = new RecordingDatabaseClient { RowsAffected = 0 };
            var store = new DeviceOperationRetryStore(database);
            var state = new DeviceOperationRetryState { Id = 42, DeviceId = 1, EmployeeId = "E1", IntentVersion = Guid.NewGuid() };
            store.TryClaimDueState(state, DateTime.Now);
            store.DeferOffline(state, "DEVICE_OFFLINE", "offline", DateTime.Now);
            store.ScheduleRetry(state, "SDK_ERROR", "retry", DateTime.Now);
            store.MarkTerminalFailure(state, "RETRY_EXHAUSTED", "stop", DateTime.Now);
            foreach (RetryOperation operation in Enum.GetValues(typeof(RetryOperation)))
            {
                store.MarkOperationSuccess(state, operation);
            }
            store.DeleteIfCompleted(state);
            store.DeleteEmptyState(state);

            foreach (var command in database.Commands)
            {
                var sql = command.CommandText.Split(new[] { " -- params:" }, StringSplitOptions.None)[0];
                Assert.Contains("WHERE id = @id AND intent_version = @intentVersion", sql, command.OperationName);
                Assert.Contains("@intentVersion=" + state.IntentVersion, command.CommandText, command.OperationName);
            }
        }

        [TestCase]
        public static void RetryStore_StalePermissionResult_DoesNotMarkUserSynced()
        {
            var database = new RecordingDatabaseClient { RowsAffected = 0 };
            var store = new DeviceOperationRetryStore(database, userSyncWriter: new SystemUserSyncStatusWriter(database));
            store.MarkOperationSuccess(new DeviceOperationRetryState
            {
                Id = 1, DeviceId = 1, EmployeeId = "E1", PermissionLevel = 5, IntentVersion = Guid.NewGuid()
            }, RetryOperation.Permission);
            Assert.False(database.Commands.Any(x => x.OperationName == "SystemUserSyncStatus.MarkPermissionSynced"));
        }

        [TestCase]
        public static void RetryStore_CurrentPermissionResult_UpdatesUserSyncFields()
        {
            var database = new RecordingDatabaseClient { RowsAffected = 1 };
            var store = new DeviceOperationRetryStore(database, userSyncWriter: new SystemUserSyncStatusWriter(database));
            store.MarkOperationSuccess(new DeviceOperationRetryState
            {
                Id = 1, DeviceId = 1, EmployeeId = "E1", PermissionLevel = 5, IntentVersion = Guid.NewGuid()
            }, RetryOperation.Permission);
            var write = database.Commands.Single(x => x.OperationName == "SystemUserSyncStatus.MarkPermissionSynced");
            Assert.Contains("last_synced_level = @permissionLevel", write.CommandText);
            Assert.Contains("@permissionLevel=5", write.CommandText);
        }

        [TestCase]
        public static void RetryStore_FailedOnlineAcknowledgement_PreservesPendingForRetry()
        {
            var database = new RecordingDatabaseClient { FailOperationName = "DeviceOperationRetryStore.MarkOperationSuccess" };
            var store = new DeviceOperationRetryStore(database);
            try
            {
                store.CompleteOnlineOperation(new DeviceOperationRetryState
                {
                    Id = 1, DeviceId = 1, EmployeeId = "E1", PersonPending = true, IntentVersion = Guid.NewGuid()
                }, RetryOperation.Person, new DeviceTaskResult { Success = true });
                throw new Exception("Expected acknowledgement error.");
            }
            catch (InvalidOperationException)
            {
                Assert.False(database.Commands.Any(x => x.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
            }
        }
    }
}
