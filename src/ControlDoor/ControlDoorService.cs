using System;
using System.ServiceProcess;
using ControlDoor.Host;

namespace ControlDoor
{
    public sealed class ControlDoorService : ServiceBase
    {
        private readonly IControlDoorHost host;
        private readonly ServiceLifecycleController lifecycle;

        public ControlDoorService()
            : this(new ControlDoorHost())
        {
        }

        public ControlDoorService(IControlDoorHost host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            lifecycle = new ServiceLifecycleController(this.host);
            ServiceName = ServiceIdentity.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            var result = lifecycle.StartAsync(TimeSpan.FromMilliseconds(120000)).GetAwaiter().GetResult();
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }
        }

        protected override void OnStop()
        {
            lifecycle.StopAsync("WindowsService", TimeSpan.FromMilliseconds(60000)).GetAwaiter().GetResult();
        }

        protected override void OnShutdown()
        {
            lifecycle.ShutdownAsync().GetAwaiter().GetResult();
            base.OnShutdown();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                host.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
