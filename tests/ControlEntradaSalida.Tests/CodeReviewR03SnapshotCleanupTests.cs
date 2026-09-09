using System;
using System.IO;
using System.Linq;
using System.Threading;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;
using ControlDoor.Runtime;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R3：历史抓拍按保留天数清理——只删超期日期目录，不触碰保留期内目录
    // 与非日期命名结构；SnapshotRetentionDays=0（默认）时后台任务空转不删除。
    public static class CodeReviewR03SnapshotCleanupTests
    {
        [TestCase]
        public static void SnapshotStorage_CleanupExpired_DeletesOnlyExpiredDateDirectories()
        {
            var runDirectory = TestWorkspace.Create();
            var root = Path.Combine(runDirectory, "snapshots");
            var storage = NewStorage(root);
            WriteFile(Path.Combine(root, "20260101", "7", "a.jpg"), 100);
            WriteFile(Path.Combine(root, "20260102", "8", "b.jpg"), 200);
            WriteFile(Path.Combine(root, "20260909", "7", "today.jpg"), 300);
            WriteFile(Path.Combine(root, "ops-manual", "keep.txt"), 400);
            WriteFile(Path.Combine(root, "20260102", "notes.txt"), 500);

            // 截止 2026-06-01：仅 20260101 与 20260102 超期（含其中非图片文件一并清理）。
            var result = storage.CleanupExpired(new DateTime(2026, 6, 1), maxFileDeletions: 100);

            Assert.Equal(3, result.DeletedFiles);
            Assert.Equal(800, result.DeletedBytes);
            Assert.Equal(0, result.FailedFiles);
            Assert.False(Directory.Exists(Path.Combine(root, "20260101")), "超期日期目录应移除。");
            Assert.False(Directory.Exists(Path.Combine(root, "20260102")), "超期日期目录应移除。");
            Assert.True(File.Exists(Path.Combine(root, "20260909", "7", "today.jpg")), "保留期内图片不得删除。");
            Assert.True(File.Exists(Path.Combine(root, "ops-manual", "keep.txt")), "非日期命名目录不得触碰。");
        }

        [TestCase]
        public static void SnapshotStorage_CleanupExpired_RespectsFileBudget()
        {
            var runDirectory = TestWorkspace.Create();
            var root = Path.Combine(runDirectory, "snapshots");
            var storage = NewStorage(root);
            WriteFile(Path.Combine(root, "20260101", "7", "a.jpg"), 100);
            WriteFile(Path.Combine(root, "20260101", "7", "b.jpg"), 100);
            WriteFile(Path.Combine(root, "20260102", "7", "c.jpg"), 100);

            var result = storage.CleanupExpired(new DateTime(2026, 6, 1), maxFileDeletions: 2);

            Assert.Equal(2, result.DeletedFiles);
            // 预算内目录被清空移除，剩余文件留待下一轮。
            Assert.Equal(1, result.RemovedDirectories);
            var remaining = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
            Assert.Equal(1, remaining.Count);
            Assert.Contains("20260102", remaining[0]);
        }

        [TestCase]
        public static void SnapshotCleanupBackgroundTask_DisabledByDefault_DoesNotRunWorker()
        {
            var runDirectory = TestWorkspace.Create();
            var root = Path.Combine(runDirectory, "snapshots");
            var storage = NewStorage(root);
            WriteFile(Path.Combine(root, "20260101", "7", "old.jpg"), 100);
            var task = new SnapshotCleanupBackgroundTask(storage, new FaceEventLoggingOptions { SnapshotRetentionDays = 0 });

            task.StartAsync(new BackgroundTaskContext("r03-disabled", CancellationToken.None, null)).GetAwaiter().GetResult();
            Thread.Sleep(150);
            task.StopAsync(new BackgroundTaskContext("r03-disabled-stop", CancellationToken.None, null)).GetAwaiter().GetResult();

            Assert.True(File.Exists(Path.Combine(root, "20260101", "7", "old.jpg")), "保留天数未配置时不得删除任何文件。");
        }

        [TestCase]
        public static void SnapshotCleanupBackgroundTask_Enabled_CleansExpiredFiles()
        {
            var runDirectory = TestWorkspace.Create();
            var root = Path.Combine(runDirectory, "snapshots");
            var storage = NewStorage(root);
            WriteFile(Path.Combine(root, "20260101", "7", "old.jpg"), 100);
            WriteFile(Path.Combine(root, DateTime.Now.ToString("yyyyMMdd"), "7", "fresh.jpg"), 100);
            var task = new SnapshotCleanupBackgroundTask(
                storage,
                new FaceEventLoggingOptions { SnapshotRetentionDays = 30, SnapshotCleanupIntervalMinutes = 5 });

            task.StartAsync(new BackgroundTaskContext("r03-enabled", CancellationToken.None, null)).GetAwaiter().GetResult();
            try
            {
                WaitUntil(() => !File.Exists(Path.Combine(root, "20260101", "7", "old.jpg")), "超期抓拍未被清理。");
                Assert.True(File.Exists(Path.Combine(root, DateTime.Now.ToString("yyyyMMdd"), "7", "fresh.jpg")), "保留期内抓拍不得删除。");
            }
            finally
            {
                task.StopAsync(new BackgroundTaskContext("r03-enabled-stop", CancellationToken.None, null)).GetAwaiter().GetResult();
            }
        }

        private static SnapshotStorage NewStorage(string root)
        {
            return new SnapshotStorage(TestWorkspace.Create(), new FaceEventLoggingOptions { SnapshotRootDirectory = root });
        }

        private static void WriteFile(string path, int size)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[size]);
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                Thread.Sleep(20);
            }

            Assert.True(condition(), message);
        }
    }
}
