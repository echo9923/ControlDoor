using System.Collections.Generic;
using System.Linq;
using ControlDoor.Configuration;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9ConfigurationValidatorTests
    {
        [TestCase]
        public static void Stage9Config_WindowSeconds_TooSmall_FallsBackToDefault()
        {
            var settings = NewSettings();
            settings.CameraAlarmDoorInterlock.WindowSeconds = 0;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(5, result.Settings.CameraAlarmDoorInterlock.WindowSeconds);
            Assert.True(result.Warnings.Any(w => w.Contains("WindowSeconds")));
        }

        [TestCase]
        public static void Stage9Config_RestoreRetryIntervalMs_TooSmall_FallsBackToDefault()
        {
            var settings = NewSettings();
            settings.CameraAlarmDoorInterlock.RestoreRetryIntervalMs = 10;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(1000, result.Settings.CameraAlarmDoorInterlock.RestoreRetryIntervalMs);
        }

        [TestCase]
        public static void Stage9Config_DoorControlSdkLockTimeoutMs_TooSmall_FallsBackToDefault()
        {
            var settings = NewSettings();
            settings.CameraAlarmDoorInterlock.DoorControlSdkLockTimeoutMs = 100;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(5000, result.Settings.CameraAlarmDoorInterlock.DoorControlSdkLockTimeoutMs);
        }

        [TestCase]
        public static void Stage9Config_EnabledMissing_DefaultsToFalse()
        {
            var settings = NewSettings();
            settings.CameraAlarmDoorInterlock.Enabled = false;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.False(result.Settings.CameraAlarmDoorInterlock.Enabled);
        }

        [TestCase]
        public static void Stage9Config_EnabledWithEmptyMappings_DoesNotFailStartup()
        {
            var settings = NewSettings();
            settings.CameraAlarmDoorInterlock.Enabled = true;
            settings.CameraAlarmDoorInterlock.Mappings = new List<CameraAlarmDoorInterlockMapping>();

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, "启用但无映射不应导致服务启动失败。");
            Assert.True(result.Warnings.Any(w => w.Contains("无有效映射")), "应记录阶段 9 自禁用告警。");
        }

        [TestCase]
        public static void Stage9Config_DoorNosMissing_DefaultsToSingleDoorOne()
        {
            var settings = NewSettings();
            settings.CameraAlarmDoorInterlock.Enabled = true;
            settings.CameraAlarmDoorInterlock.Mappings = new List<CameraAlarmDoorInterlockMapping>
            {
                new CameraAlarmDoorInterlockMapping
                {
                    Camera = new InterlockCamera { Ip = "10.0.0.5" },
                    DoorDevice = new InterlockDoorDevice { Ip = "10.0.0.10" },
                    DoorNos = new List<int>()
                }
            };

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(1, result.Settings.CameraAlarmDoorInterlock.Mappings[0].DoorNos.Count);
            Assert.Equal(1, result.Settings.CameraAlarmDoorInterlock.Mappings[0].DoorNos[0]);
        }

        [TestCase]
        public static void Stage9Config_NestedMappingModel_DeserializesViaJavaScriptSerializer()
        {
            var runDirectory = TestWorkspace.Create();
            TestWorkspace.WriteConfig(runDirectory, @"{
  ""Service"": { ""GrpcListenPort"": 5001 },
  ""Database"": { ""ConnectionString"": ""Server=.;Database=door;Password=change_me;"" },
  ""Logging"": { ""LogDirectory"": ""logs"" },
  ""DeviceRuntime"": { ""WorkerCount"": 4, ""QueueCapacity"": 1000, ""DefaultTaskTimeoutMs"": 30000 },
  ""HikvisionSdk"": { ""DllDirectory"": ""sdk\\Hikvision"", ""SdkLogDirectory"": ""logs\\sdk"" },
  ""DeviceLifecycle"": { ""StatusCheckIntervalMs"": 30000, ""LoginTimeoutMs"": 15000 },
  ""CameraAlarmDoorInterlock"": {
    ""Enabled"": true,
    ""WindowSeconds"": 8,
    ""RestoreRetryIntervalMs"": 750,
    ""Mappings"": [
      {
        ""Camera"": { ""Ip"": ""10.0.0.5"", ""Id"": 0 },
        ""DoorDevice"": { ""Ip"": ""10.0.0.10"", ""Id"": 0 },
        ""DoorNos"": [1, 2],
        ""Enabled"": true,
        ""Remark"": ""test""
      }
    ]
  }
}");

            var result = new ConfigurationLoader().Load(runDirectory);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var section = result.Settings.CameraAlarmDoorInterlock;
            Assert.True(section.Enabled);
            Assert.Equal(8, section.WindowSeconds);
            Assert.Equal(1, section.Mappings.Count);
            Assert.Equal("10.0.0.5", section.Mappings[0].Camera.Ip);
            Assert.Equal("10.0.0.10", section.Mappings[0].DoorDevice.Ip);
            Assert.Equal(2, section.Mappings[0].DoorNos.Count);
            Assert.Equal(2, section.Mappings[0].DoorNos[1]);
        }

        private static AppSettings NewSettings()
        {
            return new AppSettings
            {
                Database = new DatabaseOptions { ConnectionString = "Server=.;Database=test;Password=change_me;" },
                CameraAlarmDoorInterlock = new CameraAlarmDoorInterlockOptions
                {
                    Enabled = false,
                    Mappings = new List<CameraAlarmDoorInterlockMapping>
                    {
                        new CameraAlarmDoorInterlockMapping
                        {
                            Camera = new InterlockCamera { Ip = "10.0.0.5" },
                            DoorDevice = new InterlockDoorDevice { Ip = "10.0.0.10" },
                            DoorNos = new List<int> { 1 }
                        }
                    }
                }
            };
        }
    }
}
