using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
                // appsettings.json 允许以 // 和 /* */ 书写注释，解析前先剥离，
                // 避免 JavaScriptSerializer 把注释当作非法字符导致配置加载失败。
                // 剥离时正确处理字符串字面量与转义，确保连接串、URL 中的 // 不被误删。
                json = StripComments(json);
                json = NormalizeStage8Aliases(json);
                var serializer = new JavaScriptSerializer();
                settings = serializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                return ConfigurationLoadResult.Failed(
                    configPath,
                    new[] { "配置 JSON 解析失败: " + ex.Message });
            }

            var validation = validator.Validate(settings, runDirectory);
            if (!validation.Success)
            {
                return new ConfigurationLoadResult(false, validation.Settings, configPath, validation.Errors, validation.Warnings);
            }

            return ConfigurationLoadResult.Succeeded(validation.Settings, configPath, validation.Warnings);
        }

        private static string NormalizeStage8Aliases(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            json = Regex.Replace(json, "\"DeviceRuntime\"\\s*:", "\"DeviceSdkDispatcher\":", RegexOptions.IgnoreCase);
            json = Regex.Replace(json, "\"DeviceLifecycle\"\\s*:", "\"DeviceConnection\":", RegexOptions.IgnoreCase);
            return json;
        }

        // 剥离 JSON 文本中的 // 单行注释和 /* */ 多行注释。
        // 逐字符扫描并跟踪是否处于字符串字面量内：仅在字符串之外识别注释符，
        // 字符串内部的 //（如连接串、URL）和 \" 转义都会被原样保留。
        private static string StripComments(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            var output = new StringBuilder(json.Length);
            int i = 0;
            int length = json.Length;
            bool inString = false;

            while (i < length)
            {
                char current = json[i];

                if (inString)
                {
                    output.Append(current);
                    // 转义字符：连同其后一个字符原样保留，避免 \" 被误判为字符串结束。
                    if (current == '\\' && i + 1 < length)
                    {
                        output.Append(json[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }
                    i++;
                    continue;
                }

                // 不在字符串内：遇到引号进入字符串。
                if (current == '"')
                {
                    inString = true;
                    output.Append(current);
                    i++;
                    continue;
                }

                // 单行注释 //：跳过至行尾（换行符保留为合法空白）。
                if (current == '/' && i + 1 < length && json[i + 1] == '/')
                {
                    i += 2;
                    while (i < length && json[i] != '\n' && json[i] != '\r')
                    {
                        i++;
                    }
                    continue;
                }

                // 多行注释 /* */：跳过至结束符。
                if (current == '/' && i + 1 < length && json[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < length && !(json[i] == '*' && json[i + 1] == '/'))
                    {
                        i++;
                    }
                    i += 2;
                    if (i > length)
                    {
                        i = length;
                    }
                    continue;
                }

                output.Append(current);
                i++;
            }

            return output.ToString();
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
