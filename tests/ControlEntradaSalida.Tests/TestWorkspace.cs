using System;
using System.IO;
using System.Text;

namespace ControlEntradaSalida.Tests
{
    public static class TestWorkspace
    {
        public static string Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "ControlDoorTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public static void WriteConfig(string runDirectory, string json)
        {
            var configDirectory = Path.Combine(runDirectory, "Configuration");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "appsettings.json"), json, Encoding.UTF8);
        }
    }
}
