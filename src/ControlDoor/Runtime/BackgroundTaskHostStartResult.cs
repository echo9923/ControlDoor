using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Runtime
{
    public sealed class BackgroundTaskHostStartResult
    {
        private BackgroundTaskHostStartResult(bool success, bool partial, IEnumerable<string> errors)
        {
            Success = success;
            PartialSuccess = partial;
            Errors = (errors ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
        }

        public bool Success { get; }

        public bool PartialSuccess { get; }

        public IReadOnlyList<string> Errors { get; }

        public static BackgroundTaskHostStartResult Succeeded()
        {
            return new BackgroundTaskHostStartResult(true, false, Array.Empty<string>());
        }

        public static BackgroundTaskHostStartResult Partial(IEnumerable<string> errors)
        {
            return new BackgroundTaskHostStartResult(true, true, errors);
        }

        public static BackgroundTaskHostStartResult Failed(IEnumerable<string> errors)
        {
            return new BackgroundTaskHostStartResult(false, false, errors);
        }
    }
}
