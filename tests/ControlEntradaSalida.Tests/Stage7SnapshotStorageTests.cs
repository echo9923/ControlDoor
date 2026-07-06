using System;
using System.IO;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7SnapshotStorageTests
    {
        [TestCase]
        public static void SnapshotStorage_CreatesRootDirectory()
        {
            var workspace = TestWorkspace.Create();
            var root = Path.Combine(workspace, "snapshots");

            var storage = new SnapshotStorage(workspace, new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });

            Assert.True(Directory.Exists(root));
            Assert.Equal(root, storage.RootDirectory);
        }

        [TestCase]
        public static void SnapshotStorage_SaveJpeg_WritesFileAndReturnsAbsolutePath()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace, new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var faceEvent = NewEvent("10001", JpegBytes());

            var result = storage.Save(faceEvent);

            Assert.True(result.Saved);
            Assert.True(Path.IsPathRooted(result.SnapshotPath), "snapshot path must be absolute");
            Assert.True(File.Exists(result.SnapshotPath));
            Assert.Contains("snapshotSaved", faceEvent.RawPayload);
            Assert.Contains("10001", result.SnapshotPath);
        }

        [TestCase]
        public static void SnapshotStorage_NoPicture_ReturnsEmptyPath()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace, new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var faceEvent = NewEvent("10001", new byte[0]);

            var result = storage.Save(faceEvent);

            Assert.False(result.Saved);
            Assert.Equal("NO_PICTURE", result.ErrorCode);
            Assert.Equal(null, result.SnapshotPath);
        }

        [TestCase]
        public static void SnapshotStorage_InvalidFileNameCharacters_AreReplaced()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace, new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var faceEvent = NewEvent("10:00/1*?", JpegBytes());

            var result = storage.Save(faceEvent);

            Assert.True(result.Saved);
            var fileName = Path.GetFileName(result.SnapshotPath);
            Assert.False(fileName.Contains(":"));
            Assert.False(fileName.Contains("*"));
            Assert.False(fileName.Contains("?"));
        }

        [TestCase]
        public static void SnapshotStorage_LongEmployeeId_UsesCompactFileName()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace, new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var faceEvent = NewEvent(new string('a', 300), JpegBytes());

            var result = storage.Save(faceEvent);

            Assert.True(result.Saved);
            Assert.True(Path.IsPathRooted(result.SnapshotPath), "snapshot path must be absolute");
            Assert.True(result.SnapshotPath.Length <= 255);
        }

        [TestCase]
        public static void SnapshotStorage_UnsupportedFormat_DoesNotWriteFile()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace, new FaceEventLoggingOptions { SnapshotRootDirectory = "snapshots" });
            var faceEvent = NewEvent("10001", new byte[] { 1, 2, 3, 4 });

            var result = storage.Save(faceEvent);

            Assert.False(result.Saved);
            Assert.Equal("UNSUPPORTED_FORMAT", result.ErrorCode);
        }

        private static AcsFaceEvent NewEvent(string employeeId, byte[] pictureBytes)
        {
            var faceEvent = new AcsFaceEvent
            {
                EventId = 70000000345,
                EmployeeId = employeeId,
                EventTime = new DateTime(2026, 6, 13, 8, 9, 10, 123),
                DeviceId = 7,
                DeviceName = "Front Gate",
                DeviceSn = "SN-7",
                PictureBytes = pictureBytes
            };
            faceEvent.RawPayloadFields["source"] = "Realtime";
            faceEvent.RawPayload = "{}";
            return faceEvent;
        }

        private static byte[] JpegBytes()
        {
            return new byte[] { 0xFF, 0xD8, 0x11, 0x22, 0xFF, 0xD9 };
        }
    }
}
