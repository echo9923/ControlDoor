using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Configuration
{
    public sealed class ConfigurationLoadResult
    {
        public ConfigurationLoadResult(
            bool success,
            AppSettings settings,
            string configPath,
            IEnumerable<string> errors,
            IEnumerable<string> warnings)
        {
            Success = success;
            Settings = settings;
            ConfigPath = configPath ?? string.Empty;
            Errors = (errors ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
            Warnings = (warnings ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
        }

        public bool Success { get; }

        public AppSettings Settings { get; }

        public string ConfigPath { get; }

        public IReadOnlyList<string> Errors { get; }

        public IReadOnlyList<string> Warnings { get; }

        public static ConfigurationLoadResult Failed(string configPath, IEnumerable<string> errors, IEnumerable<string> warnings = null)
        {
            return new ConfigurationLoadResult(false, null, configPath, errors, warnings ?? Array.Empty<string>());
        }

        public static ConfigurationLoadResult Succeeded(AppSettings settings, string configPath, IEnumerable<string> warnings)
        {
            return new ConfigurationLoadResult(true, settings, configPath, Array.Empty<string>(), warnings);
        }
    }
}
