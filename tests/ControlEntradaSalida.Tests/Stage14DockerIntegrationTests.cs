using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using ControlDoor.GrpcApi;
using ControlDoor.Host;
using Grpc.Core;

namespace ControlEntradaSalida.Tests
{
    public static class Stage14DockerIntegrationTests
    {
        private const string EnableVariable = "CONTROLDOOR_STAGE14_INTEGRATION";
        private const string ConnectionStringVariable = "CONTROLDOOR_STAGE14_CONNECTION_STRING";

        [TestCase]
        public static void Stage14Integration_DockerSqlServer_HostStartsAndGrpcReturnsDisabledDevice()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            {
                Console.WriteLine("[SKIP] Set " + EnableVariable + "=1 to run the stage 1-4 Docker integration smoke test.");
                return;
            }

            var runDirectory = TestWorkspace.Create();
            var port = FindFreeTcpPort();
            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = "Server=127.0.0.1,14333;Database=ruoyi-vue-pro;User Id=door_user;Password=change_me;TrustServerCertificate=True;";
            }

            TestWorkspace.WriteConfig(runDirectory, CreateConfig(connectionString, port));
            Directory.CreateDirectory(Path.Combine(runDirectory, "logs"));
            Directory.CreateDirectory(Path.Combine(runDirectory, "snapshots"));

            using (var host = new ControlDoorHost(runDirectory))
            {
                var start = host.StartAsync().GetAwaiter().GetResult();
                Assert.True(start.Success, "Host should start with Docker SQL Server: " + start.Message);

                try
                {
                    var response = CallGetDeviceStatus(port, @"{""includeDisabled"":true}");
                    var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response);

                    Assert.Equal(true, body["success"]);
                    Assert.Equal("OK", body["code"]);
                    Assert.Contains("9001", response, "Seeded disabled placeholder device should be visible through gRPC.");
                    Assert.Contains(@"""enabled"":false", response, "Placeholder device must stay disabled before hardware is connected.");

                    var addResponse = CallGrpc(
                        port,
                        "AddDevice",
                        @"{""deviceId"":9010,""deviceName"":""阶段1-4 gRPC 新增禁用设备"",""ipAddress"":""127.0.0.251"",""port"":8000,""username"":""admin"",""password"":""change_me"",""enabled"":false,""connectNow"":false}");
                    Assert.Contains(@"""success"":true", addResponse);
                    Assert.Contains(@"""deviceId"":9010", addResponse);
                    Assert.Contains(@"""enabled"":false", addResponse);

                    var afterAdd = CallGetDeviceStatus(port, @"{""deviceId"":9010,""includeDisabled"":true}");
                    Assert.Contains(@"""deviceId"":9010", afterAdd);

                    var deleteResponse = CallGrpc(port, "DeleteDevice", @"{""deviceId"":9010}");
                    Assert.Contains(@"""success"":true", deleteResponse);
                    Assert.Contains(@"""deleted"":true", deleteResponse);
                }
                finally
                {
                    var stop = host.StopAsync("Stage14Integration").GetAwaiter().GetResult();
                    Assert.True(stop.Success, "Host should stop cleanly: " + stop.Message);
                }
            }
        }

        private static string CallGetDeviceStatus(int port, string requestJson)
        {
            return CallGrpc(port, "GetDeviceStatus", requestJson);
        }

        private static string CallGrpc(int port, string methodName, string requestJson)
        {
            var marshaller = Marshallers.Create(
                value => Encoding.UTF8.GetBytes(value ?? string.Empty),
                bytes => Encoding.UTF8.GetString(bytes ?? new byte[0]));
            var method = new Method<string, string>(
                MethodType.Unary,
                AccessControlGrpcService.ServiceName,
                methodName,
                marshaller,
                marshaller);

            var channel = new Channel("127.0.0.1:" + port, ChannelCredentials.Insecure);
            try
            {
                var invoker = new DefaultCallInvoker(channel);
                var metadata = new Metadata { { "x-request-id", "stage14-integration" } };
                return invoker.AsyncUnaryCall(
                    method,
                    null,
                    new CallOptions(metadata, DateTime.UtcNow.AddSeconds(10)),
                    requestJson).ResponseAsync.GetAwaiter().GetResult();
            }
            finally
            {
                channel.ShutdownAsync().GetAwaiter().GetResult();
            }
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

        private static string CreateConfig(string connectionString, int grpcPort)
        {
            return @"{
  ""Service"": {
    ""GrpcListenPort"": " + grpcPort + @",
    ""GrpcManagementApiKey"": """"
  },
  ""Database"": {
    ""ConnectionString"": """ + EscapeJson(connectionString) + @""",
    ""CommandTimeoutSeconds"": 30,
    ""StartupRetryCount"": 1,
    ""StartupRetryIntervalSeconds"": 1
  },
  ""Logging"": {
    ""LogDirectory"": ""logs"",
    ""RetentionDays"": 30,
    ""EnableGrpcPayloadLogging"": false,
    ""GrpcPayloadLogMode"": ""Summary"",
    ""IncludeCredentialFields"": true,
    ""IncludeFaceImageBase64"": false,
    ""EnableSdkTrace"": true
  },
  ""DeviceSdkDispatcher"": {
    ""WorkerCount"": 4,
    ""QueueCapacity"": 1000,
    ""DefaultTaskTimeoutMs"": 30000,
    ""HighPriorityQueueEnabled"": true
  },
  ""DeviceConnection"": {
    ""StatusCheckIntervalMs"": 30000,
    ""LoginTimeoutMs"": 15000,
    ""LogoutTimeoutMs"": 10000,
    ""ReconnectBaseDelayMs"": 5000,
    ""ReconnectMaxDelayMs"": 300000
  },
  ""DeviceOperationRetry"": {
    ""ScanIntervalSeconds"": 30,
    ""MaxRetryAttempts"": 10,
    ""TerminalRetentionDays"": 30
  },
  ""FaceEventLogging"": {
    ""Enabled"": true,
    ""SnapshotRootDirectory"": ""snapshots"",
    ""ExcludedDeviceIds"": [],
    ""EnableHistoryCompensation"": true
  },
  ""FaceEnrollment"": {
    ""MaxFaceImageBytes"": 204800,
    ""CaptureTimeoutSeconds"": 60,
    ""TaskRetentionMinutes"": 30
  },
  ""CameraAlarmDoorInterlock"": {
    ""Enabled"": false,
    ""Mappings"": []
  }
}";
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace(@"\", @"\\").Replace(@"""", @"\""");
        }
    }
}
