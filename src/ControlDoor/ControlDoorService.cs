using System;
using System.ServiceProcess;
using ControlDoor.Host;

namespace ControlDoor
{
    public sealed class ControlDoorService : ServiceBase
    {
        private readonly IControlDoorHost host;

        public ControlDoorService()
            : this(new ControlDoorHost())
        {
        }

        public ControlDoorService(IControlDoorHost host)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            ServiceName = ServiceIdentity.ServiceName;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            host.StartAsync().GetAwaiter().GetResult();
        }

        protected override void OnStop()
        {
            host.StopAsync("WindowsService").GetAwaiter().GetResult();
        }

        protected override void OnShutdown()
        {
            host.StopAsync("Shutdown").GetAwaiter().GetResult();
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
