using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class CodeReviewFaceUploadPollingTests
    {
        [TestCase]
        public static void NativeFaceUpload_NeedWaitThenSuccess_KeepsSessionAndReturnsAcknowledgement()
        {
            var remote = new FakeRemoteConfig(1001, 1001, 1000);
            var client = new HikvisionSdkNativeClient(remote);
            var status = Upload(client, CancellationToken.None, out var body);
            Assert.Equal(1000, status);
            Assert.Equal(FakeRemoteConfig.SuccessBody, body);
            Assert.Equal(1, remote.StartCalls);
            Assert.Equal(3, remote.SendCalls);
            Assert.Equal(1, remote.StopCalls);
        }

        [TestCase]
        public static void NativeFaceUpload_NeedWaitThenFailure_ReturnsFailureAndClosesSession()
        {
            var remote = new FakeRemoteConfig(1001, 1003) { ResponseBody = "{\"statusCode\":4}" };
            var status = Upload(new HikvisionSdkNativeClient(remote), CancellationToken.None, out var body);
            Assert.Equal(1003, status);
            Assert.Equal(remote.ResponseBody, body);
            Assert.Equal(2, remote.SendCalls);
            Assert.Equal(1, remote.StopCalls);
        }

        [TestCase]
        public static void NativeFaceUpload_NeedWaitForever_TimesOutWithoutBusySpinAndClosesSession()
        {
            var remote = new FakeRemoteConfig(1001);
            var watch = Stopwatch.StartNew();
            Stage3TestReflection.Expect<TimeoutException>(() => Upload(new HikvisionSdkNativeClient(remote, 60), CancellationToken.None, out _));
            Assert.True(watch.ElapsedMilliseconds >= 50);
            Assert.True(watch.ElapsedMilliseconds < 2000);
            Assert.True(remote.SendCalls > 0 && remote.SendCalls <= 10, "Polling must wait between NEEDWAIT results.");
            Assert.Equal(1, remote.StopCalls);
        }

        [TestCase]
        public static void NativeFaceUpload_CancelDuringWait_ClosesSession()
        {
            using (var source = new CancellationTokenSource())
            {
                var remote = new FakeRemoteConfig(1001) { AfterSend = source.Cancel };
                Stage3TestReflection.Expect<OperationCanceledException>(() => Upload(new HikvisionSdkNativeClient(remote), source.Token, out _));
                Assert.Equal(1, remote.SendCalls);
                Assert.Equal(1, remote.StopCalls);
            }
        }

        [TestCase]
        public static void NativeFaceUpload_AlreadyCancelled_DoesNotOpenSession()
        {
            var remote = new FakeRemoteConfig(1000);
            Stage3TestReflection.Expect<OperationCanceledException>(() => Upload(new HikvisionSdkNativeClient(remote), new CancellationToken(true), out _));
            Assert.Equal(0, remote.StartCalls);
            Assert.Equal(0, remote.StopCalls);
        }

        [TestCase]
        public static void NativeFaceUpload_OpenFails_DoesNotSendOrStopInvalidHandle()
        {
            var remote = new FakeRemoteConfig(1000) { Handle = -1 };
            Assert.Equal(-1, Upload(new HikvisionSdkNativeClient(remote), CancellationToken.None, out _));
            Assert.Equal(0, remote.SendCalls);
            Assert.Equal(0, remote.StopCalls);
        }

        [TestCase]
        public static void NativeFaceUpload_SendThrows_ClosesSession()
        {
            var remote = new FakeRemoteConfig(1001) { AfterSend = () => throw new InvalidOperationException("send failed") };
            Stage3TestReflection.Expect<InvalidOperationException>(() => Upload(new HikvisionSdkNativeClient(remote), CancellationToken.None, out _));
            Assert.Equal(1, remote.StopCalls);
        }

        [TestCase]
        public static void NativeFaceUpload_ImmediateTerminalStatus_DoesNotPollAgain()
        {
            foreach (var expected in new[] { -1, 1000, 1002, 1003, 1004 })
            {
                var remote = new FakeRemoteConfig(expected);
                Assert.Equal(expected, Upload(new HikvisionSdkNativeClient(remote), CancellationToken.None, out _));
                Assert.Equal(1, remote.SendCalls);
                Assert.Equal(1, remote.StopCalls);
            }
        }

        private static int Upload(HikvisionSdkNativeClient client, CancellationToken cancellationToken, out string body)
        {
            return client.UploadFaceData(1, "PUT /ISAPI/Intelligent/FDLib/FDSetUp?format=json", "{\"FPID\":\"E1\"}",
                Stage3TestReflection.JpegBytes(), cancellationToken, out body);
        }

        private sealed class FakeRemoteConfig : IHikvisionRemoteConfigNativeClient
        {
            internal const string SuccessBody = "{\"statusCode\":1,\"statusString\":\"OK\"}";
            private readonly Queue<int> statuses;
            private IntPtr firstInput;
            internal int Handle = 42;
            internal int StartCalls;
            internal int SendCalls;
            internal int StopCalls;
            internal string ResponseBody = SuccessBody;
            internal Action AfterSend;

            internal FakeRemoteConfig(params int[] statuses)
            {
                this.statuses = new Queue<int>(statuses);
            }

            public int Start(int userId, uint command, IntPtr inputBuffer, int inputBufferLength)
            {
                StartCalls++;
                Assert.Equal((uint)2551, command);
                Assert.True(inputBuffer != IntPtr.Zero && inputBufferLength > 0);
                return Handle;
            }

            public int Send(int handle, IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize, ref uint outputDataLength)
            {
                Assert.Equal(Handle, handle);
                Assert.Equal(0, StopCalls, "The session must remain open across NEEDWAIT responses.");
                if (SendCalls == 0) firstInput = inputBuffer;
                Assert.Equal(firstInput, inputBuffer);
                SendCalls++;
                var status = statuses.Count > 1 ? statuses.Dequeue() : statuses.Peek();
                var bytes = Encoding.UTF8.GetBytes(status == 1001 ? "pending" : ResponseBody);
                Marshal.Copy(bytes, 0, outputBuffer, bytes.Length);
                outputDataLength = (uint)bytes.Length;
                AfterSend?.Invoke();
                return status;
            }

            public bool Stop(int handle)
            {
                Assert.Equal(Handle, handle);
                StopCalls++;
                return true;
            }
        }
    }
}
