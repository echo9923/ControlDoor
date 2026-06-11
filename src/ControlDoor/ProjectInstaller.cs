using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace ControlDoor
{
    [RunInstaller(true)]
    public sealed class ProjectInstaller : Installer
    {
        public ProjectInstaller()
        {
            var processInstaller = new ServiceProcessInstaller
            {
                Account = ServiceAccount.LocalService
            };

            var serviceInstaller = new ServiceInstaller
            {
                ServiceName = ServiceIdentity.ServiceName,
                DisplayName = ServiceIdentity.DisplayName,
                Description = ServiceIdentity.Description,
                StartType = ServiceStartMode.Automatic
            };

            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }
}
