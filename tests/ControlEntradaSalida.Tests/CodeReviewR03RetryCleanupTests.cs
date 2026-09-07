using System;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R03：终态清理不得删除仍代表未完成权限意图的行，
    // 否则设备 B 终态失败被清理后，设备 A 的成功会把人员误标为全局权限已同步。
    public static class CodeReviewR03RetryCleanupTests
    {
        [TestCase]
        public static void DeviceOperationRetryStore_CleanupExpiredFailures_KeepsRowsWithUnfinishedIntents()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { FailureRetentionDays = 7, BatchSize = 12 });

            store.CleanupExpiredFailures(new DateTime(2026, 1, 10), 12);

            var sql = database.Commands.Last().CommandText;
            Assert.Contains("exhausted_at IS NOT NULL", sql);
            Assert.Contains("exhausted_at < @cutoff", sql);
            // 任一意图仍为待执行/阻塞的终态行都必须保留，作为全局权限完成判断的依据。
            Assert.Contains("permission_pending = 0", sql);
            Assert.Contains("permission_sync_completion_blocked = 0", sql);
            Assert.Contains("person_pending = 0", sql);
            Assert.Contains("face_pending = 0", sql);
            Assert.Contains("delete_person_pending = 0", sql);
            Assert.Contains("delete_face_pending = 0", sql);
        }
    }
}
