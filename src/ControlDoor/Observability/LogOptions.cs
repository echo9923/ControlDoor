using System;
using System.IO;
using ControlDoor.Configuration;

namespace ControlDoor.Observability
{
    public sealed class LogOptions
    {
        public string LogDirectory { get; set; } = @"D:\ControlDoorData\logs";

        public int RetentionDays { get; set; } = 30;

        public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

        public LogLevel DiagnosticMinimumLevel { get; set; } = LogLevel.Debug;

        public int DiagnosticRetentionDays { get; set; } = 7;

        public int MaxFileSizeMB { get; set; } = 20;

        public int MaxTotalSizeMB { get; set; } = 512;

        public int DiagnosticMaxTotalSizeMB { get; set; } = 2048;

        public int MaxPayloadChars { get; set; } = 16384;

        public int SlowOperationThresholdMs { get; set; } = 1000;

        public bool EnableGrpcPayloadLogging { get; set; } = true;

        public string GrpcPayloadLogMode { get; set; } = "Full";

        public bool IncludeCredentialFields { get; set; }

        public bool IncludeFaceImageBase64 { get; set; }

        public bool EnableSdkTrace { get; set; } = true;

        public bool MirrorToConsole { get; set; }

        public static LogOptions FromSettings(string runDirectory, LoggingOptions options, bool mirrorToConsole = false)
        {
            options = options ?? new LoggingOptions();
            var directory = options.LogDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = @"D:\ControlDoorData\logs";
            }

            if (!Path.IsPathRooted(directory))
            {
                directory = Path.Combine(runDirectory ?? AppDomain.CurrentDomain.BaseDirectory, directory);
            }

            return new LogOptions
            {
                LogDirectory = directory,
                RetentionDays = options.RetentionDays < 1 ? 30 : options.RetentionDays,
                MinimumLevel = LogLevelParser.ParseOrDefault(options.MinimumLevel, LogLevel.Info),
                DiagnosticMinimumLevel = LogLevelParser.ParseOrDefault(options.DiagnosticMinimumLevel, LogLevel.Debug),
                DiagnosticRetentionDays = options.DiagnosticRetentionDays < 1 ? 7 : options.DiagnosticRetentionDays,
                MaxFileSizeMB = options.MaxFileSizeMB < 1 ? 20 : options.MaxFileSizeMB,
                MaxTotalSizeMB = options.MaxTotalSizeMB < 1 ? 512 : options.MaxTotalSizeMB,
                DiagnosticMaxTotalSizeMB = options.DiagnosticMaxTotalSizeMB < 1 ? 2048 : options.DiagnosticMaxTotalSizeMB,
                MaxPayloadChars = options.MaxPayloadChars < 1 ? 16384 : options.MaxPayloadChars,
                SlowOperationThresholdMs = options.SlowOperationThresholdMs < 1 ? 1000 : options.SlowOperationThresholdMs,
                EnableGrpcPayloadLogging = options.EnableGrpcPayloadLogging,
                GrpcPayloadLogMode = options.GrpcPayloadLogMode,
                IncludeCredentialFields = options.IncludeCredentialFields,
                IncludeFaceImageBase64 = options.IncludeFaceImageBase64,
                EnableSdkTrace = options.EnableSdkTrace,
                MirrorToConsole = mirrorToConsole
            };
        }
    }
}
