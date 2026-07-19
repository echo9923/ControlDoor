using System;
using System.Collections.Generic;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class EnrollmentTaskStoreTests
    {
        [TestCase]
        public static void EnrollmentTaskStore_Start_Succeed_Fail_Roundtrip()
        {
            var store = new EnrollmentTaskStore();

            var started = store.Start("task-1", "emp-1");
            Assert.Equal(EnrollmentTaskStatus.Running, started.Status);

            var fetched = store.GetByTaskId("task-1");
            Assert.NotNull(fetched);
            Assert.Equal(EnrollmentTaskStatus.Running, fetched.Status);

            store.Succeed("task-1", "OK");
            Assert.Equal(EnrollmentTaskStatus.Succeeded, store.GetByTaskId("task-1").Status);

            store.Fail("task-2", "ERR", "失败");
            Assert.True(store.GetByTaskId("task-2") == null);
        }

        [TestCase]
        public static void EnrollmentTaskStore_GetLatestByEmployeeId_ReturnsLatest()
        {
            var store = new EnrollmentTaskStore();

            store.Start("task-A", "emp-X");
            store.Succeed("task-A", "done");
            store.Start("task-B", "emp-X");

            var latest = store.GetLatestByEmployeeId("emp-X");
            Assert.NotNull(latest);
            Assert.Equal("task-B", latest.TaskId);
            Assert.Equal(EnrollmentTaskStatus.Running, latest.Status);
        }

        [TestCase]
        public static void EnrollmentTaskStore_EvictsRecordsBeyondCapacity()
        {
            // 容量上限 3，插入 5 条已结束任务应只保留最近 3 条（按 CreatedAt 升序淘汰最旧）。
            var store = new EnrollmentTaskStore(maxRecords: 3, retention: TimeSpan.FromHours(24));

            for (var i = 0; i < 5; i++)
            {
                var id = "task-" + i.ToString("D2");
                store.Start(id, "emp-" + i);
                store.Succeed(id, "ok");
            }

            var all = store.GetAll();
            Assert.Equal(3, all.Count);
            // 最旧的 task-00、task-01 应被淘汰。
            Assert.True(store.GetByTaskId("task-00") == null);
            Assert.True(store.GetByTaskId("task-01") == null);
            Assert.NotNull(store.GetByTaskId("task-02"));
            Assert.NotNull(store.GetByTaskId("task-03"));
            Assert.NotNull(store.GetByTaskId("task-04"));
        }

        [TestCase]
        public static void EnrollmentTaskStore_EvictsExpiredTerminalRecords()
        {
            // retention 设为 1ms，结束任务应几乎立刻过期；Running 不应被过期清除。
            var store = new EnrollmentTaskStore(maxRecords: 200, retention: TimeSpan.FromMilliseconds(1));

            store.Start("running-task", "emp-r");
            store.Start("succeeded-task", "emp-s");
            store.Succeed("succeeded-task", "done");
            store.Start("failed-task", "emp-f");
            store.Fail("failed-task", "ERR", "失败");

            // 等待超过 retention。
            System.Threading.Thread.Sleep(20);

            // 触发清理（Start 会再次进入并清理）。
            store.Start("trigger-task", "emp-t");

            // 已结束任务应被清理（UpdatedAt 远早于 cutoff）。
            Assert.True(store.GetByTaskId("succeeded-task") == null);
            Assert.True(store.GetByTaskId("failed-task") == null);
            // Running 不应因 retention 被清理。
            Assert.NotNull(store.GetByTaskId("running-task"));
            Assert.NotNull(store.GetByTaskId("trigger-task"));
        }

        [TestCase]
        public static void EnrollmentTaskStore_Eviction_CleansUpLatestByEmployeeIndex()
        {
            // latestByEmployee 中指向已被淘汰任务的索引必须同步移除，
            // 否则 GetLatestByEmployeeId 会拿到不存在的 taskId。
            var store = new EnrollmentTaskStore(maxRecords: 1, retention: TimeSpan.FromHours(24));

            store.Start("task-only", "emp-only");
            store.Succeed("task-only", "done");

            // 再插入一条新任务，触发超容量淘汰 task-only。
            store.Start("task-new", "emp-new");

            // emp-only 的任务已被淘汰，GetLatestByEmployeeId 应返回 null 而非抛异常。
            Assert.True(store.GetLatestByEmployeeId("emp-only") == null);
            var latestNew = store.GetLatestByEmployeeId("emp-new");
            Assert.NotNull(latestNew);
            Assert.Equal("task-new", latestNew.TaskId);
        }

        [TestCase]
        public static void EnrollmentTaskStore_GetOperations_TriggerEvictionButDoNotReturnEvictedRecords()
        {
            var store = new EnrollmentTaskStore(maxRecords: 2, retention: TimeSpan.FromHours(24));

            store.Start("task-1", "emp-1");
            store.Start("task-2", "emp-2");
            store.Start("task-3", "emp-3");

            // 容量 2，task-1 应已被淘汰。
            Assert.Equal(2, store.GetAll().Count);
            Assert.True(store.GetByTaskId("task-1") == null);
        }

        [TestCase]
        public static void EnrollmentTaskStore_Update_TriggersEviction()
        {
            // 容量 2：插入两条 Running，再把最早那条 Succeed 后再插入新任务，验证 Update 也触发清理。
            var store = new EnrollmentTaskStore(maxRecords: 2, retention: TimeSpan.FromHours(24));

            store.Start("t1", "e1");
            store.Start("t2", "e2");

            // 容量已达上限。Update t1 后容量仍 2（无插入），不应淘汰。
            store.Succeed("t1", "done");
            Assert.Equal(2, store.GetAll().Count);
            Assert.NotNull(store.GetByTaskId("t1"));

            // 插入新任务触发清理，最旧的 t1 应被淘汰。
            store.Start("t3", "e3");
            Assert.True(store.GetByTaskId("t1") == null);
            Assert.NotNull(store.GetByTaskId("t2"));
            Assert.NotNull(store.GetByTaskId("t3"));
        }
    }
}
