using System;
using System.Collections.Generic;

namespace ControlDoor.Host
{
    public sealed class HostStartupResult
    {
        private HostStartupResult(bool success, string message, IReadOnlyList<string> errors)
        {
            Success = success;
            Message = message ?? string.Empty;
            Errors = errors ?? Array.Empty<string>();
        }

        public bool Success { get; }

        public string Message { get; }

        public IReadOnlyList<string> Errors { get; }

        public static HostStartupResult Succeeded(string message)
        {
            return new HostStartupResult(true, message, Array.Empty<string>());
        }

        public static HostStartupResult Failed(string message, IReadOnlyList<string> errors)
        {
            return new HostStartupResult(false, message, errors);
        }
    }
}
