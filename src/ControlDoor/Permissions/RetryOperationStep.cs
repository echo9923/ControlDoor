namespace ControlDoor.Permissions
{
    public sealed class RetryOperationStep
    {
        public RetryOperationStep(RetryOperation operation)
        {
            Operation = operation;
        }

        public RetryOperation Operation { get; }

        public string OperationName => RetryOperationNames.ToStage5OperationName(Operation);

        public string RetryCategory => RetryOperationNames.ToRetryCategory(Operation);
    }
}
