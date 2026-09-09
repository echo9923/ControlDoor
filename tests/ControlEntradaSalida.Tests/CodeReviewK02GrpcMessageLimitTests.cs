using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlDoor.Configuration;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Workers;
using ControlDoor.GrpcApi;
using ControlDoor.Hikvision;
using ControlDoor.Runtime;
using Grpc.Core;

namespace ControlEntradaSalida.Tests
{
    // 代码复核 K2：合法人脸批量请求不能先被 gRPC 传输层 ResourceExhausted 拒绝。
    // 业务层按单请求 base64 总量返回 REQUEST_TOO_LARGE，服务端显式设置受控接收上限。
    public static class CodeReviewK02GrpcMessageLimitTests
    {
        [TestCase]
        public static void SyncPersons_BatchFaceBudgetExceeded_ReturnsRequestTooLarge()
        {
            using (var fixture = new Stage5Fixture(null, 5000, new FaceEnrollmentOptions { MaxBatchFaceBytes = 4 * 1024 }))
            {
                fixture.AddOnlineDevice();
                var face = Convert.ToBase64String(new byte[1536]);
                var people = string.Join(",", new[] { 1, 2, 3 }.Select(index => @"{""employee_id"":""E" + index + @""",""face_image_base64"":""" + face + @"""}"));

                var response = fixture.Response(fixture.Service.SyncPersons(@"{""people"":[" + people + @"]}", fixture.Context("k02-budget")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("REQUEST_TOO_LARGE", response["code"]);
                Assert.Contains("拆小批次", Convert.ToString(response["message"]));
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
            }
        }

        [TestCase]
        public static void SyncFacesToDevices_BatchFaceBudgetExceeded_ReturnsRequestTooLarge()
        {
            using (var fixture = new Stage5Fixture(null, 5000, new FaceEnrollmentOptions { MaxBatchFaceBytes = 4 * 1024 }))
            {
                fixture.AddOnlineDevice();
                var face = Convert.ToBase64String(new byte[1536]);
                var people = string.Join(",", new[] { 1, 2, 3 }.Select(index => @"{""employee_id"":""E" + index + @""",""face_image_base64"":""" + face + @"""}"));

                var response = fixture.Response(fixture.Service.SyncFacesToDevices(@"{""deviceIds"":[1],""people"":[" + people + @"]}", fixture.Context("k02-targeted-budget")));

                Assert.Equal(false, response["success"]);
                Assert.Equal("REQUEST_TOO_LARGE", response["code"]);
                Assert.Equal(0, fixture.RetryWriter.Intents.Count);
            }
        }

        [TestCase]
        public static void GrpcServer_RealChannel_BusinessBudgetFailsBeforeTransportLimit()
        {
            using (var harness = new K02ServerHarness(transportLimitBytes: 1024 * 1024, batchBudgetChars: 32 * 1024))
            {
                // 预算（32 KiB）< 请求（约 90 KiB）< 传输上限（1 MiB）：必须返回业务错误 REQUEST_TOO_LARGE，
                // 而不是传输层 ResourceExhausted。
                var face = Convert.ToBase64String(new byte[64 * 1024]);
                var request = @"{""deviceIds"":[1],""people"":[{""employee_id"":""E1"",""face_image_base64"":""" + face + @"""}]}";
                var response = harness.Call("SyncFacesToDevices", request);

                Assert.Contains("REQUEST_TOO_LARGE", response);
            }
        }

        [TestCase]
        public static void GrpcServer_RealChannel_RequestAboveTransportLimit_IsRejectedByTransport()
        {
            using (var harness = new K02ServerHarness(transportLimitBytes: 128 * 1024, batchBudgetChars: 32 * 1024))
            {
                // 请求（约 700 KiB）超过传输上限（128 KiB）：传输层返回 ResourceExhausted，
                // 这是文档化的最终边界，业务层无法给出更友好的错误。
                var face = Convert.ToBase64String(new byte[512 * 1024]);
                var request = @"{""deviceIds"":[1],""people"":[{""employee_id"":""E1"",""face_image_base64"":""" + face + @"""}]}";
                var rpcException = harness.ExpectRpcFailure("SyncFacesToDevices", request);

                Assert.Equal(StatusCode.ResourceExhausted, rpcException.Status.StatusCode);
            }
        }

        [TestCase]
        public static void GrpcServer_RealChannel_SmallRequestPassesTransport()
        {
            using (var harness = new K02ServerHarness(transportLimitBytes: 1024 * 1024, batchBudgetChars: 32 * 1024))
            {
                var face = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 });
                var request = @"{""deviceIds"":[1],""people"":[{""employee_id"":""E1"",""face_image_base64"":""" + face + @"""}]}";
                var response = harness.Call("SyncFacesToDevices", request);

                // 小请求应穿过传输层到达业务层（无设备时返回业务结果而非传输错误）。
                Assert.False(response.Contains("REQUEST_TOO_LARGE"));
            }
        }

        private sealed class K02ServerHarness : IDisposable
        {
            private readonly GrpcServerBackgroundTask serverTask;
            private readonly int port;

            public K02ServerHarness(int transportLimitBytes, int batchBudgetChars)
            {
                var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 1 });
                var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 1, queueCapacityPerWorker: 10, defaultTaskTimeoutMilliseconds: 5000);
                var gateway = new MockHikvisionGateway();
                var repository = new InMemoryDeviceRepository();
                var lifecycle = new DeviceLifecycleService(registry, dispatcher, null, repository, gateway, new DeviceLifecycleOptions { AlarmEnabled = false });
                var accessControl = new AccessControlGrpcService(lifecycle, repository);
                var permissionSync = new PermissionSyncGrpcService(
                    registry,
                    dispatcher,
                    gateway,
                    faceEnrollment: new FaceEnrollmentOptions { MaxBatchFaceBytes = batchBudgetChars });
                port = FindFreeTcpPort();
                serverTask = new GrpcServerBackgroundTask(port, accessControl, permissionSync, transportLimitBytes);
                serverTask.StartAsync(new BackgroundTaskContext("k02-server", System.Threading.CancellationToken.None, null)).GetAwaiter().GetResult();
            }

            public string Call(string methodName, string requestJson)
            {
                var channel = new Channel("127.0.0.1:" + port, ChannelCredentials.Insecure);
                try
                {
                    return Invoke(channel, methodName, requestJson);
                }
                finally
                {
                    channel.ShutdownAsync().GetAwaiter().GetResult();
                }
            }

            public RpcException ExpectRpcFailure(string methodName, string requestJson)
            {
                var channel = new Channel("127.0.0.1:" + port, ChannelCredentials.Insecure);
                try
                {
                    try
                    {
                        Invoke(channel, methodName, requestJson);
                    }
                    catch (RpcException exception)
                    {
                        return exception;
                    }

                    throw new InvalidOperationException("预期传输层拒绝，但调用成功了。");
                }
                finally
                {
                    channel.ShutdownAsync().GetAwaiter().GetResult();
                }
            }

            private static string Invoke(Channel channel, string methodName, string requestJson)
            {
                var marshaller = Marshallers.Create(
                    value => Encoding.UTF8.GetBytes(value ?? string.Empty),
                    bytes => Encoding.UTF8.GetString(bytes ?? new byte[0]));
                var method = new Method<string, string>(
                    MethodType.Unary,
                    PermissionSyncGrpcService.ServiceName,
                    methodName,
                    marshaller,
                    marshaller);
                var invoker = new DefaultCallInvoker(channel);
                return invoker.AsyncUnaryCall(
                    method,
                    null,
                    new CallOptions(new Metadata { { "x-request-id", "k02-test" } }, DateTime.UtcNow.AddSeconds(20)),
                    requestJson).ResponseAsync.GetAwaiter().GetResult();
            }

            private static int FindFreeTcpPort()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    return ((IPEndPoint)listener.LocalEndpoint).Port;
                }
                finally
                {
                    listener.Stop();
                }
            }

            public void Dispose()
            {
                serverTask.StopAsync(new BackgroundTaskContext("k02-server-stop", System.Threading.CancellationToken.None, null)).GetAwaiter().GetResult();
            }
        }
    }
}
