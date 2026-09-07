using System;
using System.IO;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 R05：同一事件多次入库重试不得重复落盘抓拍，避免产生无数据库引用的孤立图片。
    public static class CodeReviewR05SnapshotStorageTests
    {
        [TestCase]
        public static void SnapshotStorage_SaveRepeatedForSameEvent_ReusesExistingFile()
        {
            var runDirectory = TestWorkspace.Create();
            var snapshotRoot = Path.Combine(runDirectory, "snapshots");
            var storage = new SnapshotStorage(runDirectory, new FaceEventLoggingOptions { SnapshotRootDirectory = snapshotRoot });

            // 模拟三次插入失败后一次成功：每次重试都会重新构造事件对象并再次保存。
            string firstPath = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = storage.Save(NewEvent());
                Assert.True(result.Saved, "保存失败: " + result.ErrorMessage);
                if (firstPath == null)
                {
                    firstPath = result.SnapshotPath;
                }
                else
                {
                    Assert.Equal(firstPath, result.SnapshotPath);
                }
            }

            Assert.Equal(1, Directory.EnumerateFiles(snapshotRoot, "*.jpg", SearchOption.AllDirectories).Count());
            Assert.True(File.Exists(firstPath));
            Assert.True(new FileInfo(firstPath).Length > 0);
        }

        [TestCase]
        public static void SnapshotStorage_SaveDifferentEvents_CreatesSeparateFiles()
        {
            var runDirectory = TestWorkspace.Create();
            var snapshotRoot = Path.Combine(runDirectory, "snapshots");
            var storage = new SnapshotStorage(runDirectory, new FaceEventLoggingOptions { SnapshotRootDirectory = snapshotRoot });

            var first = storage.Save(NewEvent());
            var second = storage.Save(NewEvent(eventId: 990002));

            Assert.True(first.Saved);
            Assert.True(second.Saved);
            Assert.False(string.Equals(first.SnapshotPath, second.SnapshotPath, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, Directory.EnumerateFiles(snapshotRoot, "*.jpg", SearchOption.AllDirectories).Count());
        }

        private static AcsFaceEvent NewEvent(long eventId = 990001)
        {
            return new AcsFaceEvent
            {
                EventId = eventId,
                EmployeeId = "10001",
                EventTime = new DateTime(2026, 9, 7, 12, 30, 15, 123),
                DeviceId = 7,
                PictureBytes = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0x03, 0xFF, 0xD9 }
            };
        }
    }
}
