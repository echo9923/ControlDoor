using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlDoor.Configuration;
using System.Web.Script.Serialization;
using ControlDoor.Devices.Management;
using ControlDoor.Devices.Runtime;
using ControlDoor.Devices.Workers;
using ControlDoor.GrpcApi;
using ControlDoor.Runtime;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage4GrpcContractTests
    {
        [TestCase]
        public static void AccessControlGrpcService_MethodFullNames_MatchContract()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.GetDeviceStatusFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.AddDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.DeleteDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.DisconnectDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.ReconnectDeviceFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.RearmDeviceAlarmFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.DisarmDeviceAlarmFullName));
                Assert.True(service.MethodFullNames.Contains(AccessControlGrpcService.GetDeviceAlarmStatusFullName));
            }
        }

        [TestCase]
        public static void GrpcServerBackgroundTask_StartAsync_AcceptsLargeMessageContract()
        {
            // FACE-02 回归：500 人+ 人脸契约需要放宽 MaxReceive/MaxSendMessageLength；StartAsync 不能因消息上限配置抛异常。
            // 端口 0 让 OS 分配空闲端口，避免与其他用例冲突；启动后立即关闭验证生命周期完整。
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);
                var task = new GrpcServerBackgroundTask(0, service);
                var context = new BackgroundTaskContext("face02-grpc-size", System.Threading.CancellationToken.None, null);

                try
                {
                    task.StartAsync(context).GetAwaiter().GetResult();
                    var status = task.GetStatus();
                    Assert.True(status.IsRunning);
                }
                finally
                {
                    task.StopAsync(context).GetAwaiter().GetResult();
                }
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_ReturnsUnifiedDeviceFields()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(description: "办公区域");
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus("{}", new GrpcRequestContext { RequestId = "req-status" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.True(response.ContainsKey("requestId"));
                Assert.True(response.ContainsKey("devices"));
                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal("办公区域", device["description"]);
                Assert.True(device.ContainsKey("types"));
                Assert.True(device.ContainsKey("isAlarmArmed"));
                Assert.True(device.ContainsKey("alarmStatus"));
                Assert.True(device.ContainsKey("alarmStatusMessage"));
                Assert.Equal(false, device["isAlarmArmed"]);
                Assert.Equal("Unavailable", device["alarmStatus"]);
                Assert.Equal("设备不可布防", device["alarmStatusMessage"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_IncludeDisabledControlsJsonOnlyDevices()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                fixture.AddRecord(2, "10.0.4.2", enabled: false);
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var included = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":true}", new GrpcRequestContext { RequestId = "req-disabled-1" }));
                var filtered = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":false}", new GrpcRequestContext { RequestId = "req-disabled-2" }));

                var includedDevices = (System.Collections.ICollection)included["devices"];
                var filteredDevices = (System.Collections.ICollection)filtered["devices"];
                Assert.Equal(2, includedDevices.Count);
                Assert.Equal(1, filteredDevices.Count);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_InvalidEnabledDevice_ReturnsInvalidConfigNotLoaded()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                var record = fixture.Repository.GetByDeviceId(1);
                record.Password = string.Empty;
                fixture.Repository.Add(record);
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":true}", new GrpcRequestContext { RequestId = "req-invalid-status" }));

                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal("InvalidConfig", device["status"]);
                Assert.Equal("INVALID_CONFIG", device["lastErrorCode"]);
                Assert.True(device["lastErrorMessage"].ToString().Contains("password"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_InvalidEnabledRepositoryDevice_KeepsEnabledIntent()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: true);
                var record = fixture.Repository.GetByDeviceId(1);
                record.Password = string.Empty;
                fixture.Repository.Add(record);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":true}", new GrpcRequestContext { RequestId = "req-invalid-status-repo" }));

                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(true, device["enabled"]);
                Assert.Equal("InvalidConfig", device["status"]);
                Assert.Equal("INVALID_CONFIG", device["lastErrorCode"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_AlarmHandleReturnsArmedStatus()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Registry.RegisterSdkUserId(1, 1001, "serial-1", System.DateTime.Now);
                fixture.Registry.RegisterAlarmHandle(1, 9001, System.DateTime.Now);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-armed" }));

                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(true, device["isAlarmArmed"]);
                Assert.Equal("Armed", device["alarmStatus"]);
                Assert.Equal("已布防", device["alarmStatusMessage"]);
                Assert.False(device.ContainsKey("alarmHandle"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_OnlineWithoutAlarmHandleReturnsNotArmed()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Registry.RegisterSdkUserId(1, 1001, "serial-1", System.DateTime.Now);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-not-armed" }));

                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(false, device["isAlarmArmed"]);
                Assert.Equal("NotArmed", device["alarmStatus"]);
                Assert.Equal("在线但未布防", device["alarmStatusMessage"]);
                Assert.False(device.ContainsKey("alarmHandle"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_ManualDisarmReturnsManualAlarmStatus()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                fixture.Lifecycle.DisarmDeviceAlarm(1, "req-disarm");
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-manual-disarm" }));

                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(false, device["isAlarmArmed"]);
                Assert.Equal("ManuallyDisarmed", device["alarmStatus"]);
                Assert.Equal("已手动撤防", device["alarmStatusMessage"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_DisabledJsonDeviceReturnsUnavailableAlarmStatus()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: false);
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""includeDisabled"":true}", new GrpcRequestContext { RequestId = "req-disabled-alarm" }));

                var devices = (System.Collections.ArrayList)response["devices"];
                var device = (Dictionary<string, object>)devices[0];
                Assert.Equal(false, device["isAlarmArmed"]);
                Assert.Equal("Unavailable", device["alarmStatus"]);
                Assert.Equal("设备不可布防", device["alarmStatusMessage"]);
                Assert.False(device.ContainsKey("alarmHandle"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceStatus_MissingDeviceReturnsNotFound()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceStatus(@"{""deviceId"":77}", new GrpcRequestContext { RequestId = "req-missing" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("NOT_FOUND", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_AcceptsAliasesAndDefaults()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(@"{""device_id"":9,""device_name"":""北门"",""ip_address"":""10.0.4.9"",""password"":""12345"",""types"":[""Acs"",""FaceCapture""],""connectNow"":false}", new GrpcRequestContext { RequestId = "req-add" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.True(fixture.Registry.TryGetByDeviceId(9).Found);
                var record = fixture.Repository.GetByDeviceId(9);
                Assert.True(record.Types.Contains(DeviceType.Acs));
                Assert.True(record.Types.Contains(DeviceType.FaceCapture));
                var device = (Dictionary<string, object>)response["device"];
                Assert.True(device.ContainsKey("types"));
                Assert.Equal(string.Empty, device["description"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_PersistsToJsonDeviceList()
        {
            using (var fixture = JsonBackedFixture.Create("{\"devices\":[]}"))
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(@"{""deviceId"":101,""deviceName"":""JSON门禁"",""ipAddress"":""10.0.4.101"",""password"":""12345"",""types"":[""Acs""],""connectNow"":false}", new GrpcRequestContext { RequestId = "req-add-json" }));

                Assert.Equal(true, response["success"]);
                Assert.True(fixture.Registry.TryGetByDeviceId(101).Found);
                var persistedJson = File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8);
                Assert.Contains("\"deviceId\": 101", persistedJson);
                Assert.Contains("\"ipAddress\": \"10.0.4.101\"", persistedJson);
                Assert.Contains("\"types\"", persistedJson);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_DeleteDevice_RemovesFromJsonDeviceList()
        {
            using (var fixture = JsonBackedFixture.Create(
                "{\"devices\":[{\"deviceId\":101,\"name\":\"JSON门禁\",\"types\":[\"Acs\"],\"ipAddress\":\"10.0.4.101\",\"port\":8000,\"username\":\"admin\",\"password\":\"12345\",\"enabled\":true}]}"))
            {
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.DeleteDevice(@"{""deviceId"":101,""disconnectFirst"":false}", new GrpcRequestContext { RequestId = "req-delete-json" }));

                Assert.Equal(true, response["success"]);
                Assert.False(fixture.Registry.TryGetByDeviceId(101).Found);
                var persistedJson = File.ReadAllText(fixture.DevicesFilePath, Encoding.UTF8);
                Assert.False(persistedJson.Contains("\"deviceId\": 101"));
                Assert.Equal(0, fixture.Repository.LoadAllDevices().Count);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_RequiresTypes()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(@"{""device_id"":9,""device_name"":""北门"",""ip_address"":""10.0.4.9"",""password"":""12345""}", new GrpcRequestContext { RequestId = "req-add-types" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("types 不能为空", (string)response["message"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_RejectsInvalidTypes()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(@"{""device_id"":9,""device_name"":""北门"",""ip_address"":""10.0.4.9"",""password"":""12345"",""types"":[""1""]}", new GrpcRequestContext { RequestId = "req-add-invalid-types" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("types 含非法值", (string)response["message"]);
                Assert.False(fixture.Registry.TryGetByDeviceId(9).Found);
            }
        }

        // ===== AddDevice 对齐 main 项目（必填校验 + port 字符串 + PARTIAL_SUCCESS + 隐藏 types）=====

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_MissingDeviceId_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_name"":""北门"",""ip_address"":""10.0.4.9"",""password"":""12345"",""types"":[""Acs""]}",
                    new GrpcRequestContext { RequestId = "req-add-no-id" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("deviceId", (string)response["message"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_MissingDeviceName_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":12,""ip_address"":""10.0.4.12"",""password"":""12345"",""types"":[""Acs""]}",
                    new GrpcRequestContext { RequestId = "req-add-no-name" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("deviceName", (string)response["message"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_MissingIpAddress_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":13,""device_name"":""侧门"",""password"":""12345"",""types"":[""Acs""]}",
                    new GrpcRequestContext { RequestId = "req-add-no-ip" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("ipAddress", (string)response["message"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_MissingPassword_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":14,""device_name"":""后门"",""ip_address"":""10.0.4.14"",""types"":[""Acs""]}",
                    new GrpcRequestContext { RequestId = "req-add-no-pwd" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("password", (string)response["message"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_PortAsString_Accepted()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":20,""device_name"":""port门"",""ip_address"":""10.0.4.20"",""port"":""8080"",""password"":""12345"",""types"":[""Acs""],""connectNow"":false}",
                    new GrpcRequestContext { RequestId = "req-add-port-str" }));

                Assert.Equal(true, response["success"]);
                var device = (Dictionary<string, object>)response["device"];
                // 响应 port 现在是字符串（对齐 main）
                Assert.Equal("8080", device["port"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_InvalidPort_ReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":21,""device_name"":""badport"",""ip_address"":""10.0.4.21"",""port"":""abc"",""password"":""12345"",""types"":[""Acs""]}",
                    new GrpcRequestContext { RequestId = "req-add-bad-port" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
                Assert.Contains("port", (string)response["message"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_ResponseContainsTypesAndDescription()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":22,""device_name"":""types门"",""ip_address"":""10.0.4.22"",""password"":""12345"",""types"":[""Acs"",""Camera""],""connectNow"":false}",
                    new GrpcRequestContext { RequestId = "req-add-no-types-resp" }));

                Assert.Equal(true, response["success"]);
                var device = (Dictionary<string, object>)response["device"];
                Assert.True(device.ContainsKey("types"));
                Assert.True(device.ContainsKey("description"));
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AddDevice_ConnectNowFailure_ReturnsPartialSuccess()
        {
            using (var fixture = new Stage4Fixture())
            {
                // 注入登录失败：ConfigureException 让 LoginAsync 抛 DeviceGatewayException。
                fixture.Gateway.ConfigureException("LoginAsync", new DeviceGatewayException("Login", SdkError.FromCode(1)));
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.AddDevice(
                    @"{""device_id"":30,""device_name"":""连不上"",""ip_address"":""10.0.4.30"",""password"":""12345"",""types"":[""Acs""],""connectNow"":true}",
                    new GrpcRequestContext { RequestId = "req-add-connect-fail" }));

                // 设备已入库但连接失败：success=true, code=PARTIAL_SUCCESS, connected=false
                Assert.Equal(true, response["success"]);
                Assert.Equal("PARTIAL_SUCCESS", response["code"]);
                Assert.Equal(false, response["connected"]);
                Assert.True(fixture.Registry.TryGetByDeviceId(30).Found);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_ApiKey_RejectsInvalidMetadata()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository, "secret");

                var response = Deserialize(service.GetDeviceStatus("{}", new GrpcRequestContext { RequestId = "req-auth" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("UNAUTHENTICATED", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AlarmOperations_ApiKeyRejectsInvalidMetadata()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository, "secret");

                var rearm = Deserialize(service.RearmDeviceAlarm(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-rearm-auth" }));
                var disarm = Deserialize(service.DisarmDeviceAlarm(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-disarm-auth" }));

                Assert.Equal(false, rearm["success"]);
                Assert.Equal("UNAUTHENTICATED", rearm["code"]);
                Assert.Equal(false, disarm["success"]);
                Assert.Equal("UNAUTHENTICATED", disarm["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceAlarmStatus_ApiKeyRejectsInvalidMetadata()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository, "secret");

                var response = Deserialize(service.GetDeviceAlarmStatus(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-alarm-status-auth" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("UNAUTHENTICATED", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_DeleteDevice_IsIdempotentSuccess()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.DeleteDevice(@"{""deviceId"":99}", new GrpcRequestContext { RequestId = "req-delete" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(true, response["deleted"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_ReconnectMissingDevice_ReturnsNotFound()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.ReconnectDevice(@"{""device_id"":99}", new GrpcRequestContext { RequestId = "req-reconnect" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("NOT_FOUND", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AlarmOperations_InvalidDeviceIdReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var rearm = Deserialize(service.RearmDeviceAlarm(@"{""deviceId"":0}", new GrpcRequestContext { RequestId = "req-rearm-invalid" }));
                var disarm = Deserialize(service.DisarmDeviceAlarm(@"{""device_id"":0}", new GrpcRequestContext { RequestId = "req-disarm-invalid" }));

                Assert.Equal(false, rearm["success"]);
                Assert.Equal("INVALID_ARGUMENT", rearm["code"]);
                Assert.Equal(false, disarm["success"]);
                Assert.Equal("INVALID_ARGUMENT", disarm["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_AlarmOperations_MissingDeviceReturnsNotFound()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var rearm = Deserialize(service.RearmDeviceAlarm(@"{""device_id"":99}", new GrpcRequestContext { RequestId = "req-rearm-missing" }));
                var disarm = Deserialize(service.DisarmDeviceAlarm(@"{""device_id"":99}", new GrpcRequestContext { RequestId = "req-disarm-missing" }));

                Assert.Equal(false, rearm["success"]);
                Assert.Equal("NOT_FOUND", rearm["code"]);
                Assert.Equal(false, disarm["success"]);
                Assert.Equal("NOT_FOUND", disarm["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceAlarmStatus_InvalidDeviceIdReturnsInvalidArgument()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceAlarmStatus(@"{""device_id"":0}", new GrpcRequestContext { RequestId = "req-alarm-status-invalid" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("INVALID_ARGUMENT", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceAlarmStatus_MissingDeviceReturnsNotFound()
        {
            using (var fixture = new Stage4Fixture())
            {
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.GetDeviceAlarmStatus(@"{""device_id"":99}", new GrpcRequestContext { RequestId = "req-alarm-status-missing" }));

                Assert.Equal(false, response["success"]);
                Assert.Equal("NOT_FOUND", response["code"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceAlarmStatus_ArmedDeviceReturnsHandleAndArmedStatus()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Registry.RegisterSdkUserId(1, 1001, "serial-1", System.DateTime.Now);
                fixture.Registry.RegisterAlarmHandle(1, 9001, System.DateTime.Now);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);
                var sdkCallCount = fixture.Gateway.Calls.Count;

                var response = Deserialize(service.GetDeviceAlarmStatus(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-alarm-status-armed" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, response["deviceId"]);
                Assert.Equal(true, response["armed"]);
                Assert.Equal("Armed", response["alarmStatus"]);
                Assert.Equal("已布防", response["alarmStatusMessage"]);
                Assert.Equal(9001, response["alarmHandle"]);
                Assert.Equal(true, response["connected"]);
                Assert.Equal("Online", response["status"]);
                Assert.Equal(sdkCallCount, fixture.Gateway.Calls.Count);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceAlarmStatus_OnlineWithoutAlarmHandleReturnsNotArmed()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Registry.RegisterSdkUserId(1, 1001, "serial-1", System.DateTime.Now);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);
                var sdkCallCount = fixture.Gateway.Calls.Count;

                var response = Deserialize(service.GetDeviceAlarmStatus(@"{""device_id"":1}", new GrpcRequestContext { RequestId = "req-alarm-status-not-armed" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(false, response["armed"]);
                Assert.Equal("NotArmed", response["alarmStatus"]);
                Assert.Equal("在线但未布防", response["alarmStatusMessage"]);
                Assert.Equal(null, response["alarmHandle"]);
                Assert.Equal(true, response["connected"]);
                Assert.Equal("Online", response["status"]);
                Assert.Equal(sdkCallCount, fixture.Gateway.Calls.Count);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_GetDeviceAlarmStatus_DisabledOrOfflineReturnsUnavailable()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord(1, "10.0.4.1", enabled: false);
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);
                var sdkCallCount = fixture.Gateway.Calls.Count;

                var response = Deserialize(service.GetDeviceAlarmStatus(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-alarm-status-unavailable" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(false, response["armed"]);
                Assert.Equal("Unavailable", response["alarmStatus"]);
                Assert.Equal("设备不可布防", response["alarmStatusMessage"]);
                Assert.Equal(null, response["alarmHandle"]);
                Assert.Equal(false, response["connected"]);
                Assert.Equal("Disabled", response["status"]);
                Assert.Equal(sdkCallCount, fixture.Gateway.Calls.Count);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_DisarmDeviceAlarm_ReturnsAlarmStateWithoutDisconnecting()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.DisarmDeviceAlarm(@"{""deviceId"":1}", new GrpcRequestContext { RequestId = "req-disarm" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, response["deviceId"]);
                Assert.Equal(false, response["armed"]);
                Assert.Equal(null, response["alarmHandle"]);
                Assert.Equal("ManuallyDisarmed", response["alarmStatus"]);
                Assert.Equal("已手动撤防", response["alarmStatusMessage"]);
                Assert.Equal(true, response["connected"]);
                Assert.Equal("Online", response["status"]);
            }
        }

        [TestCase]
        public static void AccessControlGrpcService_RearmDeviceAlarm_ReturnsNewAlarmHandle()
        {
            using (var fixture = new Stage4Fixture())
            {
                fixture.AddRecord();
                fixture.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                fixture.Lifecycle.SubmitLogin(1, wait: true, requestId: "req-login");
                WaitUntil(() => fixture.Registry.TryGetByDeviceId(1).Snapshot.AlarmHandle.HasValue, "alarm was not armed.");
                fixture.Lifecycle.DisarmDeviceAlarm(1, "req-pre-disarm");
                var service = new AccessControlGrpcService(fixture.Lifecycle, fixture.Repository);

                var response = Deserialize(service.RearmDeviceAlarm(@"{""device_id"":1}", new GrpcRequestContext { RequestId = "req-rearm" }));

                Assert.Equal(true, response["success"]);
                Assert.Equal("OK", response["code"]);
                Assert.Equal(1, response["deviceId"]);
                Assert.Equal(true, response["armed"]);
                Assert.NotNull(response["alarmHandle"]);
                Assert.Equal(true, response["connected"]);
                Assert.Equal("Online", response["status"]);
            }
        }

        private static void WaitUntil(System.Func<bool> condition, string message)
        {
            var deadline = System.DateTime.UtcNow.AddSeconds(2);
            while (System.DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(20);
            }

            Assert.True(condition(), message);
        }

        private static System.Collections.Generic.Dictionary<string, object> Deserialize(string json)
        {
            return new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
        }

        private sealed class JsonBackedFixture : System.IDisposable
        {
            private JsonBackedFixture(
                string runDirectory,
                JsonDeviceRepository repository,
                DeviceRuntimeRegistry registry,
                DeviceSdkDispatcher dispatcher,
                DelayedDeviceTaskScheduler delayedScheduler,
                MockHikvisionGateway gateway,
                DeviceLifecycleService lifecycle)
            {
                RunDirectory = runDirectory;
                Repository = repository;
                Registry = registry;
                Dispatcher = dispatcher;
                DelayedScheduler = delayedScheduler;
                Gateway = gateway;
                Lifecycle = lifecycle;
                DevicesFilePath = Path.Combine(runDirectory, "Configuration", "devices.json");
            }

            public string RunDirectory { get; }

            public string DevicesFilePath { get; }

            public JsonDeviceRepository Repository { get; }

            public DeviceRuntimeRegistry Registry { get; }

            public DeviceSdkDispatcher Dispatcher { get; }

            public DelayedDeviceTaskScheduler DelayedScheduler { get; }

            public MockHikvisionGateway Gateway { get; }

            public DeviceLifecycleService Lifecycle { get; }

            public static JsonBackedFixture Create(string devicesJson)
            {
                var runDirectory = TestWorkspace.Create();
                var configurationDirectory = Path.Combine(runDirectory, "Configuration");
                Directory.CreateDirectory(configurationDirectory);
                File.WriteAllText(Path.Combine(configurationDirectory, "devices.json"), devicesJson, Encoding.UTF8);

                var repository = new JsonDeviceRepository(
                    runDirectory,
                    new DeviceStoreOptions
                    {
                        FilePath = "Configuration\\devices.json",
                        BackupOnWrite = true,
                        Items = new List<DeviceStoreItem>()
                    });
                var registry = new DeviceRuntimeRegistry(new DeviceRuntimeRegistryOptions { WorkerCount = 2 });
                var dispatcher = new DeviceSdkDispatcher(registry, workerCount: 2, queueCapacityPerWorker: 50, defaultTaskTimeoutMilliseconds: 5000);
                var delayedScheduler = new DelayedDeviceTaskScheduler(dispatcher, new DelayedDeviceTaskSchedulerOptions { WakeupMaxSleepMilliseconds = 10 });
                var gateway = new MockHikvisionGateway();
                var options = new DeviceLifecycleOptions
                {
                    LoginTimeoutMs = 5000,
                    LogoutTimeoutMs = 5000,
                    HealthCheckIntervalMs = 1000,
                    HealthCheckTimeoutMs = 5000,
                    ReconnectBaseDelayMs = 10,
                    ReconnectMaxDelayMs = 100,
                    MaxReconnectAttempts = 3,
                    FailureThreshold = 3,
                    AlarmEnabled = true
                };
                var lifecycle = new DeviceLifecycleService(registry, dispatcher, delayedScheduler, repository, gateway, options);

                return new JsonBackedFixture(runDirectory, repository, registry, dispatcher, delayedScheduler, gateway, lifecycle);
            }

            public void Dispose()
            {
                Dispatcher.StopAsync(System.TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Gateway.Dispose();
                Lifecycle.Dispose();
            }
        }
    }
}
