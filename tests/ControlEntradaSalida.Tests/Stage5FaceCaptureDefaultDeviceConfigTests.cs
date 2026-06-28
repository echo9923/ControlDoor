using System.Linq;
using ControlDoor.Configuration;

namespace ControlEntradaSalida.Tests
{
    // 验证 Devices.DefaultFaceCaptureDeviceId 的启动期/--validate-config 校验：
    //   引用不存在、<=0、指向未声明 FaceCapture 类型的设备，均应报错。
    public static class Stage5FaceCaptureDefaultDeviceConfigTests
    {
        [TestCase]
        public static void ConfigurationValidator_DefaultFaceCaptureDeviceId_NotSet_SucceedsDeviceStoreSection()
        {
            var settings = DeviceStoreSettings(defaultFaceCaptureDeviceId: null);

            var result = new ConfigurationValidator().Validate(settings);

            // 仅断言默认设备相关校验不产生任何错误（向后兼容：缺省即合法）。
            Assert.False(result.Errors.Any(item => item.Contains("DefaultFaceCaptureDeviceId")));
        }

        [TestCase]
        public static void ConfigurationValidator_DefaultFaceCaptureDeviceId_ReferencesExistingFaceCaptureDevice_NoDeviceError()
        {
            var settings = DeviceStoreSettings(defaultFaceCaptureDeviceId: 2);
            settings.Devices.Items.Add(NewItem(deviceId: 2, types: "FaceCapture"));

            var result = new ConfigurationValidator().Validate(settings);

            Assert.False(result.Errors.Any(item => item.Contains("DefaultFaceCaptureDeviceId")));
        }

        [TestCase]
        public static void ConfigurationValidator_DefaultFaceCaptureDeviceId_NonPositive_IsRejected()
        {
            var settings = DeviceStoreSettings(defaultFaceCaptureDeviceId: 0);

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Errors.Contains("Devices.DefaultFaceCaptureDeviceId 必须大于 0。"));
        }

        [TestCase]
        public static void ConfigurationValidator_DefaultFaceCaptureDeviceId_ReferencesMissingDevice_IsRejected()
        {
            var settings = DeviceStoreSettings(defaultFaceCaptureDeviceId: 99);
            settings.Devices.Items.Add(NewItem(deviceId: 2, types: "FaceCapture"));

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Errors.Contains("Devices.DefaultFaceCaptureDeviceId=99 引用的设备不存在。"));
        }

        [TestCase]
        public static void ConfigurationValidator_DefaultFaceCaptureDeviceId_TargetNotFaceCapture_IsRejected()
        {
            var settings = DeviceStoreSettings(defaultFaceCaptureDeviceId: 2);
            settings.Devices.Items.Add(NewItem(deviceId: 2, types: "Acs"));

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Errors.Contains("Devices.DefaultFaceCaptureDeviceId=2 指向的设备未声明 FaceCapture 类型，无法用于人脸采集。"));
        }

        private static AppSettings DeviceStoreSettings(int? defaultFaceCaptureDeviceId)
        {
            var settings = new AppSettings();
            settings.Devices = new DeviceStoreOptions
            {
                FilePath = "Configuration\\devices.json",
                Items = new System.Collections.Generic.List<DeviceStoreItem>(),
                DefaultFaceCaptureDeviceId = defaultFaceCaptureDeviceId
            };
            return settings;
        }

        private static DeviceStoreItem NewItem(int deviceId, string types)
        {
            return new DeviceStoreItem
            {
                DeviceId = deviceId,
                Name = "device-" + deviceId,
                IpAddress = "10.30.0." + deviceId,
                Port = 8000,
                Password = "pw",
                Enabled = true,
                Types = new System.Collections.Generic.List<string> { types }
            };
        }
    }
}
