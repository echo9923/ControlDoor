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

        // CFG-03: ReArmBaseDelayMs 必须 > 0；ReArmMaxDelayMs 必须 >= ReArmBaseDelayMs。
        [TestCase]
        public static void Stage9Config_ReArmBaseDelayMs_TooSmall_FallsBackToDefault()
        {
            var settings = NewSettings();
            settings.DeviceConnection.ReArmBaseDelayMs = 0;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(1000, result.Settings.DeviceConnection.ReArmBaseDelayMs);
            Assert.True(result.Warnings.Any(w => w.Contains("ReArmBaseDelayMs")));
        }

        [TestCase]
        public static void Stage9Config_ReArmBaseDelayMs_Negative_FallsBackToDefault()
        {
            var settings = NewSettings();
            settings.DeviceConnection.ReArmBaseDelayMs = -500;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(1000, result.Settings.DeviceConnection.ReArmBaseDelayMs);
            Assert.True(result.Warnings.Any(w => w.Contains("ReArmBaseDelayMs")));
        }

        [TestCase]
        public static void Stage9Config_ReArmMaxDelayMs_LessThanBase_FallsBackToDefault()
        {
            var settings = NewSettings();
            settings.DeviceConnection.ReArmBaseDelayMs = 5000;
            settings.DeviceConnection.ReArmMaxDelayMs = 1000;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(60000, result.Settings.DeviceConnection.ReArmMaxDelayMs);
            Assert.True(result.Warnings.Any(w => w.Contains("ReArmMaxDelayMs")));
        }

        [TestCase]
        public static void Stage9Config_ReArmDelays_ValidValues_Unchanged()
        {
            var settings = NewSettings();
            settings.DeviceConnection.ReArmBaseDelayMs = 2000;
            settings.DeviceConnection.ReArmMaxDelayMs = 30000;

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(2000, result.Settings.DeviceConnection.ReArmBaseDelayMs);
            Assert.Equal(30000, result.Settings.DeviceConnection.ReArmMaxDelayMs);
            Assert.False(result.Warnings.Any(w => w.Contains("ReArmBaseDelayMs") || w.Contains("ReArmMaxDelayMs")));
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
