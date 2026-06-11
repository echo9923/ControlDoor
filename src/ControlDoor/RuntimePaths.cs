using System;
using System.IO;
using System.Reflection;

namespace ControlDoor
{
    public static class RuntimePaths
    {
        public const string ConfigurationRelativePath = "Configuration";
        public const string AppSettingsFileName = "appsettings.json";

        public static string GetRunDirectory()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrWhiteSpace(location))
            {
                var directory = Path.GetDirectoryName(location);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return directory;
                }
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        public static string GetConfigPath(string runDirectory)
        {
            return Path.Combine(runDirectory, ConfigurationRelativePath, AppSettingsFileName);
        }
    }
}
