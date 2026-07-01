using System;
using System.IO;
using ControlDoor.Configuration;
using ControlDoor.FaceEvents;

namespace ControlEntradaSalida.Tests
{
    public static class Stage7SnapshotStorageEdgeTests
    {
        [TestCase]
        public static void SnapshotStorage_SameEventTwice_DoesNotOverwrite()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent("10001", JpegBytes());

            var first = storage.Save(faceEvent);
            var second = storage.Save(faceEvent);

            Assert.True(first.Saved);
            Assert.True(second.Saved);
            Assert.True(first.SnapshotPath != second.SnapshotPath, "collision must produce a distinct path");
            Assert.Contains("_1", second.SnapshotPath);
            Assert.True(File.Exists(first.SnapshotPath));
            Assert.True(File.Exists(second.SnapshotPath));
        }

        [TestCase]
        public static void SnapshotStorage_NoPicture_TagsRawPayloadWithError()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent("10001", new byte[0]);

            var result = storage.Save(faceEvent);

            Assert.False(result.Saved);
            Assert.Equal("NO_PICTURE", result.ErrorCode);
            Assert.Contains("\"snapshotSaved\":false", faceEvent.RawPayload);
            Assert.Contains("NO_PICTURE", faceEvent.RawPayload);
        }

        [TestCase]
        public static void SnapshotStorage_UnsupportedFormat_TagsRawPayloadWithError()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent("10001", new byte[] { 1, 2, 3, 4 });

            var result = storage.Save(faceEvent);

            Assert.False(result.Saved);
            Assert.Equal("UNSUPPORTED_FORMAT", result.ErrorCode);
            Assert.Contains("\"snapshotSaved\":false", faceEvent.RawPayload);
            Assert.Contains("UNSUPPORTED_FORMAT", faceEvent.RawPayload);
        }

        [TestCase]
        public static void SnapshotStorage_EmptyEmployeeId_UsesUnknownSegment()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent(string.Empty, JpegBytes());

            var result = storage.Save(faceEvent);

            Assert.True(result.Saved);
            Assert.Contains("unknown", result.SnapshotPath);
        }

        [TestCase]
        public static void SnapshotStorage_MinimalFourByteJpeg_Saved()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent("10001", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

            var result = storage.Save(faceEvent);

            Assert.True(result.Saved);
            Assert.True(File.Exists(result.SnapshotPath));
        }

        [TestCase]
        public static void SnapshotStorage_TwoBytePayload_UnsupportedFormat()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent("10001", new byte[] { 0xFF, 0xD8 });

            var result = storage.Save(faceEvent);

            Assert.False(result.Saved);
            Assert.Equal("UNSUPPORTED_FORMAT", result.ErrorCode);
        }

        [TestCase]
        public static void SnapshotStorage_ReturnedPath_IsAbsoluteFilePath()
        {
            var workspace = TestWorkspace.Create();
            var storage = new SnapshotStorage(workspace);
            var faceEvent = NewEvent("10001", JpegBytes());

            var result = storage.Save(faceEvent);

            Assert.True(result.Saved);
            Assert.True(Path.IsPathRooted(result.SnapshotPath), "snapshot path must be absolute");
            Assert.True(result.SnapshotPath.StartsWith(storage.RootDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(result.SnapshotPath));
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
