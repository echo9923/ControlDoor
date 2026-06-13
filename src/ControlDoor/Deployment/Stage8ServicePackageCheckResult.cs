using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Deployment
{
    public sealed class Stage8ServicePackageCheckResult
    {
        private readonly List<Stage8ServicePackageCheckItem> items = new List<Stage8ServicePackageCheckItem>();

        public IReadOnlyList<Stage8ServicePackageCheckItem> Items => items.AsReadOnly();

        public bool Success => items.All(item => item.Success);

        public void Add(Stage8ServicePackageCheckItem item)
        {
            items.Add(item);
        }
    }
}
