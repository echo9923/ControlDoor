using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ControlDoor.Configuration
{
    public sealed class ConfigurationLoader
    {
        private readonly ConfigurationValidator validator;

        public ConfigurationLoader()
            : this(new ConfigurationValidator())
        {
        }

        public ConfigurationLoader(ConfigurationValidator validator)
        {
            this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        public ConfigurationLoadResult Load(string runDirectory)
        {
            runDirectory = ResolveRunDirectory(runDirectory);
            var configPath = RuntimePaths.GetConfigPath(runDirectory);

            if (!File.Exists(configPath))
            {
                return ConfigurationLoadResult.Failed(
                    configPath,
                    new[] { "配置文件不存在: " + configPath });
            }

            string json;
            try
            {
                json = File.ReadAllText(configPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return ConfigurationLoadResult.Failed(
                    configPath,
                    new[] { "读取配置文件失败: " + ex.Message });
            }

            AppSettings settings;
            try
            {
                var serializer = new JavaScriptSerializer();
                settings = serializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                return ConfigurationLoadResult.Failed(
                    configPath,
                    new[] { "配置 JSON 解析失败: " + ex.Message });
            }

            var validation = validator.Validate(settings);
            if (!validation.Success)
            {
                return new ConfigurationLoadResult(false, validation.Settings, configPath, validation.Errors, validation.Warnings);
            }

            return ConfigurationLoadResult.Succeeded(validation.Settings, configPath, validation.Warnings);
        }

        private static string ResolveRunDirectory(string runDirectory)
        {
            if (!string.IsNullOrWhiteSpace(runDirectory))
            {
                return Path.GetFullPath(runDirectory);
            }

            return RuntimePaths.GetRunDirectory();
        }
    }
}
