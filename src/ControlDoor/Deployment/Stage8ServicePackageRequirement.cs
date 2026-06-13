namespace ControlDoor.Deployment
{
    public sealed class Stage8ServicePackageRequirement
    {
        public Stage8ServicePackageRequirement(string relativePath, bool isDirectory, bool required)
        {
            RelativePath = relativePath;
            IsDirectory = isDirectory;
            Required = required;
        }

        public string RelativePath { get; }

        public bool IsDirectory { get; }

        public bool Required { get; }
    }
}
