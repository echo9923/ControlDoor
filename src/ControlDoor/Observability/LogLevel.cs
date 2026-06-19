using System;

namespace ControlDoor.Observability
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public static class LogLevelParser
    {
        public static LogLevel ParseOrDefault(string value, LogLevel fallback = LogLevel.Info)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            LogLevel parsed;
            return Enum.TryParse(value.Trim(), ignoreCase: true, result: out parsed) ? parsed : fallback;
        }

        public static string NormalizeOrDefault(string value, string fallback = "Info")
        {
            return ParseOrDefault(value, ParseOrDefault(fallback)).ToString();
        }
    }
}
