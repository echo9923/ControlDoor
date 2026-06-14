using System;
using System.Text;
using ControlDoor.CameraDoorInterlock;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9AiopVideoPayloadParserTests
    {
        [TestCase]
        public static void Stage9Parser_ValidBuffer_SucceedsAndExtractsFields()
        {
            var json = "{\"errorcode\":0,\"targets\":[{\"obj\":{\"type\":2,\"modelID\":\"825b148172704f19\"}}],\"events\":{\"alertInfo\":[{\"target\":{\"type\":2,\"modelID\":\"825b148172704f19\"}}]}}";
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9 };
            var buffer = BuildAiopBuffer(json, jpeg, taskId: "cbb1298adf100001");

            var payload = new AiopVideoPayloadParser().Parse(buffer, 7, "10.0.0.5");

            Assert.True(payload.ParseSucceeded, payload.ParseError);
            Assert.Equal(0x4021, payload.Command);
            Assert.Equal(7, payload.CameraDeviceId);
            Assert.Equal("10.0.0.5", payload.CameraIp);
            Assert.Equal(Encoding.UTF8.GetBytes(json).Length, payload.JsonLength);
            Assert.Equal(jpeg.Length, payload.ImageLength);
            Assert.True(payload.ImageIsJpeg);
            Assert.Equal("cbb1298adf100001", payload.TaskId);
            Assert.True(payload.DetectedTypes.Contains("2"));
            Assert.Equal("825b148172704f19", payload.ModelId);
        }

        [TestCase]
        public static void Stage9Parser_LengthMismatch_ReturnsFailedRecordWithoutThrowing()
        {
            var buffer = BuildAiopBuffer("{}", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            var shortened = new byte[buffer.Length - 5];
            Buffer.BlockCopy(buffer, 0, shortened, 0, shortened.Length);

            var payload = new AiopVideoPayloadParser().Parse(shortened, 0, "10.0.0.5");

            Assert.False(payload.ParseSucceeded);
            Assert.NotNull(payload.ParseError);
            Assert.Equal(0x4021, payload.Command);
        }

        [TestCase]
        public static void Stage9Parser_TooShortBuffer_ReturnsFailedRecordWithoutThrowing()
        {
            var payload = new AiopVideoPayloadParser().Parse(new byte[] { 1, 2, 3 }, 0, "10.0.0.5");

            Assert.False(payload.ParseSucceeded);
            Assert.NotNull(payload.ParseError);
        }

        [TestCase]
        public static void Stage9Parser_NullBuffer_ReturnsFailedRecordWithoutThrowing()
        {
            var payload = new AiopVideoPayloadParser().Parse(null, 0, "10.0.0.5");

            Assert.False(payload.ParseSucceeded);
        }

        [TestCase]
        public static void Stage9Parser_MalformedJson_StillReturnsLayoutSuccessRecord()
        {
            var brokenJson = "{ this is not valid json ";
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            var buffer = BuildAiopBuffer(brokenJson, jpeg);

            var payload = new AiopVideoPayloadParser().Parse(buffer, 0, "10.0.0.5");

            Assert.True(payload.ParseSucceeded, "合法布局应解析成功；JSON 内容解析失败不阻断。");
            Assert.Equal(0, payload.DetectedTypes.Count);
            Assert.Equal(string.Empty, payload.ModelId);
        }

        [TestCase]
        public static void Stage9Parser_NonJpegImage_NotFlaggedAsJpeg()
        {
            var pngLike = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
            var buffer = BuildAiopBuffer("{}", pngLike);

            var payload = new AiopVideoPayloadParser().Parse(buffer, 0, "10.0.0.5");

            Assert.True(payload.ParseSucceeded);
            Assert.False(payload.ImageIsJpeg);
        }

        private static byte[] BuildAiopBuffer(string json, byte[] image, string taskId = "taskid0000000001")
        {
            const int headerLen = 376;
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var header = new byte[headerLen];

            WriteUInt(header, 0, headerLen);
            WriteUInt(header, 4, 1);
            var taskIdBytes = Encoding.ASCII.GetBytes(taskId);
            for (var i = 0; i < taskIdBytes.Length && i < 16; i++)
            {
                header[24 + i] = taskIdBytes[i];
            }

            WriteUInt(header, 88, (uint)jsonBytes.Length);
            WriteUInt(header, 92, (uint)image.Length);

            var buffer = new byte[headerLen + jsonBytes.Length + image.Length];
            Buffer.BlockCopy(header, 0, buffer, 0, headerLen);
            Buffer.BlockCopy(jsonBytes, 0, buffer, headerLen, jsonBytes.Length);
            Buffer.BlockCopy(image, 0, buffer, headerLen + jsonBytes.Length, image.Length);
            return buffer;
        }

        private static void WriteUInt(byte[] buffer, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 4);
        }
    }
}
