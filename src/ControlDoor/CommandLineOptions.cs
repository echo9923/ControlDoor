using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor
{
    public enum RunMode
    {
        Service,
        Console,
        ValidateConfig,
        Version,
        Help
    }

    public sealed class CommandLineOptions
    {
        public CommandLineOptions(RunMode mode, IReadOnlyList<string> arguments)
        {
            Mode = mode;
            Arguments = arguments;
        }

        public RunMode Mode { get; }

        public IReadOnlyList<string> Arguments { get; }

        public static CommandLineOptions Parse(string[] args, bool userInteractive)
        {
            args = args ?? Array.Empty<string>();
            var normalized = new HashSet<string>(args.Select(Normalize), StringComparer.OrdinalIgnoreCase);

            if (normalized.Contains("--help") || normalized.Contains("-h") || normalized.Contains("/?"))
            {
                return new CommandLineOptions(RunMode.Help, args);
            }

            if (normalized.Contains("--version") || normalized.Contains("-v"))
            {
                return new CommandLineOptions(RunMode.Version, args);
            }

            if (normalized.Contains("--validate-config"))
            {
                return new CommandLineOptions(RunMode.ValidateConfig, args);
            }

            if (normalized.Contains("--console") || userInteractive)
            {
                return new CommandLineOptions(RunMode.Console, args);
            }

            return new CommandLineOptions(RunMode.Service, args);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim();
        }
    }
}
