using System.Linq;
using ControlDoor.CameraDoorInterlock;
using ControlDoor.Devices.Tasks;
using ControlDoor.Hikvision;

namespace ControlEntradaSalida.Tests
{
    public static class Stage9DoorControlTaskFactoryTests
    {
        [TestCase]
        public static void Stage9TaskFactory_AlwaysClose_HasHighPriorityAndCorrectOperation()
        {
            var factory = new DoorControlTaskFactory(new MockHikvisionGateway());

            var task = factory.CreateAlwaysClose(10, 1, "10:1", "req-1");

            Assert.Equal(DeviceTaskType.ControlGateway, task.TaskType);
            Assert.Equal(DeviceTaskPriority.High, task.Priority);
            Assert.Equal(DoorControlTaskFactory.AlwaysCloseOperation, task.OperationName);
            Assert.True(task.RequiresOnline);
            Assert.Equal("10:1:AlwaysClose", task.IdempotencyKey);
            Assert.Contains("door=1", task.Payload.PayloadSummary);
            Assert.Contains("AlwaysClose", task.Payload.PayloadSummary);
        }

        [TestCase]
        public static void Stage9TaskFactory_Restore_HasCriticalPriorityAndCorrectOperation()
        {
            var factory = new DoorControlTaskFactory(new MockHikvisionGateway());

            var task = factory.CreateRestore(10, 2, "10:2", "req-2", attempt: 1);

            Assert.Equal(DeviceTaskType.ControlGateway, task.TaskType);
            Assert.Equal(DeviceTaskPriority.Critical, task.Priority);
            Assert.Equal(DoorControlTaskFactory.RestoreOperation, task.OperationName);
            Assert.Equal("10:2:Restore", task.IdempotencyKey);
            Assert.True(task.RetrySource.IsRetry);
            Assert.Equal(1, task.RetrySource.RetryAttempt);
        }

        [TestCase]
        public static void Stage9TaskFactory_AlwaysClose_Executed_SendsCorrectGatewayCommand()
        {
            using (var inner = new Stage4Fixture())
            {
                LoginDoor(inner, 10, "10.0.0.10");
                var factory = new DoorControlTaskFactory(inner.Gateway);
                var task = factory.CreateAlwaysClose(10, 1, "10:1", "req");

                var result = inner.Dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();

                Assert.True(result.Success, result.Message);
                var call = inner.Gateway.Calls.Single(c => c.MethodName == "ControlGatewayAsync");
                var request = (GateControlRequest)call.Request;
                Assert.Equal(GateControlCommand.AlwaysClose, request.Command);
                Assert.Equal(1, request.GateIndex);
            }
        }

        [TestCase]
        public static void Stage9TaskFactory_Restore_Executed_SendsRestoreCommand()
        {
            using (var inner = new Stage4Fixture())
            {
                LoginDoor(inner, 10, "10.0.0.10");
                var factory = new DoorControlTaskFactory(inner.Gateway);
                var task = factory.CreateRestore(10, 1, "10:1", "req", attempt: 0);

                var result = inner.Dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();

                Assert.True(result.Success, result.Message);
                var request = (GateControlRequest)inner.Gateway.Calls.Single(c => c.MethodName == "ControlGatewayAsync").Request;
                Assert.Equal(GateControlCommand.Restore, request.Command);
                Assert.Equal(0, (int)request.Command);
            }
        }

        [TestCase]
        public static void Stage9TaskFactory_OfflineDevice_DoesNotInvokeGateway()
        {
            using (var inner = new Stage4Fixture())
            {
                inner.AddRecord(10, "10.0.0.10");
                inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
                var factory = new DoorControlTaskFactory(inner.Gateway);
                var task = factory.CreateAlwaysClose(10, 1, "10:1", "req");

                var result = inner.Dispatcher.SubmitAndWaitAsync(task).GetAwaiter().GetResult();

                Assert.False(result.Success);
                Assert.Equal(0, inner.Gateway.Calls.Count(c => c.MethodName == "ControlGatewayAsync"));
            }
        }

        [TestCase]
        public static void Stage9TaskFactory_TransientSdkErrors_AreRetryable()
        {
            using (var inner = new Stage4Fixture())
            {
                LoginDoor(inner, 10, "10.0.0.10");
                var factory = new DoorControlTaskFactory(inner.Gateway);
                foreach (var sdkErrorCode in new[] { 7, 8, 9, 10, 12, 13, 15, 20, 41, 43, 52, 408, 500 })
                {
                    inner.Gateway.ConfigureException("ControlGatewayAsync", new DeviceGatewayException("ControlGateway", SdkError.FromCode(sdkErrorCode, "transient")));

                    var result = inner.Dispatcher.SubmitAndWaitAsync(factory.CreateAlwaysClose(10, 1, "10:1", "req-" + sdkErrorCode)).GetAwaiter().GetResult();

                    Assert.False(result.Success);
                    Assert.Equal(sdkErrorCode, result.SdkErrorCode.Value);
                    Assert.True(result.Retryable, "SDK error " + sdkErrorCode + " must be retryable.");
                }
            }
        }

        private static void LoginDoor(Stage4Fixture inner, int deviceId, string ip)
        {
            inner.AddRecord(deviceId, ip);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
            var login = inner.Lifecycle.SubmitLogin(deviceId, wait: true, requestId: "stage9-taskfactory-login-" + deviceId);
            Assert.True(login.Success, login.Message);
        }
    }
}
