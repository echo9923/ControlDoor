using System;

namespace ControlDoor.Permissions
{
    public enum RetryOperation
    {
        Permission = 0,
        Person = 1,
        Face = 2,
        DeleteFace = 3,
        DeletePerson = 4
    }

    public static class RetryOperationNames
    {
        public const string SyncPermission = "SyncPermission";
        public const string SyncPerson = "SyncPerson";
        public const string UploadFace = "UploadFace";
        public const string DeleteFace = "DeleteFace";
        public const string DeletePerson = "DeletePerson";

        public static bool TryParse(string value, out RetryOperation operation)
        {
            switch ((value ?? string.Empty).Trim())
            {
                case "Permission":
                case "SyncPermission":
                    operation = RetryOperation.Permission;
                    return true;
                case "Person":
                case "SyncPerson":
                    operation = RetryOperation.Person;
                    return true;
                case "Face":
                case "UploadFace":
                    operation = RetryOperation.Face;
                    return true;
                case "DeleteFace":
                    operation = RetryOperation.DeleteFace;
                    return true;
                case "DeletePerson":
                    operation = RetryOperation.DeletePerson;
                    return true;
                default:
                    operation = RetryOperation.Permission;
                    return false;
            }
        }

        public static string ToStage5OperationName(RetryOperation operation)
        {
            switch (operation)
            {
                case RetryOperation.Permission:
                    return SyncPermission;
                case RetryOperation.Person:
                    return SyncPerson;
                case RetryOperation.Face:
                    return UploadFace;
                case RetryOperation.DeleteFace:
                    return DeleteFace;
                case RetryOperation.DeletePerson:
                    return DeletePerson;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        public static string ToRetryCategory(RetryOperation operation)
        {
            switch (operation)
            {
                case RetryOperation.Permission:
                    return "Permission";
                case RetryOperation.Person:
                    return "Person";
                case RetryOperation.Face:
                    return "Face";
                case RetryOperation.DeleteFace:
                    return "DeleteFace";
                case RetryOperation.DeletePerson:
                    return "DeletePerson";
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }
    }
}
