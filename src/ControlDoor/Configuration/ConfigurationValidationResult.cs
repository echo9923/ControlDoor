using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Configuration
{
    public sealed class ConfigurationValidationResult
    {
        public ConfigurationValidationResult(AppSettings settings, IEnumerable<string> errors, IEnumerable<string> warnings)
        {
            Settings = settings;
            Errors = (errors ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
            Warnings = (warnings ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
        }

        public AppSettings Settings { get; }

        public IReadOnlyList<string> Errors { get; }

        public IReadOnlyList<string> Warnings { get; }

        public bool Success => Errors.Count == 0;
    }
}
