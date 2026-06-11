using System.Threading.Tasks;

namespace ControlDoor.Runtime
{
    public interface IBackgroundTask
    {
        string Name { get; }

        bool IsCritical { get; }

        Task StartAsync(BackgroundTaskContext context);

        Task StopAsync(BackgroundTaskContext context);

        BackgroundTaskStatus GetStatus();
    }
}
