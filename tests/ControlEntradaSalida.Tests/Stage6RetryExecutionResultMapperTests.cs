using System.Collections.Generic;
using System.Linq;
using ControlDoor.Devices.Tasks;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage6RetryExecutionResultMapperTests
    {
        [TestCase]
        public static void RetryExecutionResultMapper_NullState_Throws()
        {
            var mapper = new RetryExecutionResultMapper();

            Stage3TestReflection.Expect<System.ArgumentNullException>(() =>
                mapper.Map(null, Plan(NewState(), RetryOperation.Person), Enumerable.Empty<DeviceTaskResult>()));
        }

        [TestCase]
        public static void RetryExecutionResultMapper_EmptyPlanAndResults_ReportsAllSucceeded()
        {
            var mapper = new RetryExecutionResultMapper();
            var state = NewState();

            var result = mapper.Map(state, Plan(state), Enumerable.Empty<DeviceTaskResult>());

            Assert.True(result.AllSucceeded);
            Assert.Equal("OK", result.Code);
        }

        [TestCase]
        public static void RetryExecutionResultMapper_NullResult_ReportsInternalError()
        {
            var mapper = new RetryExecutionResultMapper();
            var state = NewState();
            var plan = Plan(state, RetryOperation.Person);

            var result = mapper.Map(state, plan, new DeviceTaskResult[] { null });

            Assert.False(result.AllSucceeded);
            Assert.Equal(RetryOperation.Person, result.FailedOperation);
            Assert.False(result.Retryable);
            Assert.Equal("INTERNAL_ERROR", result.Code);
        }

        [TestCase]
        public static void RetryExecutionResultMapper_FirstFailure_StopsAndPropagatesCode()
        {
            var mapper = new RetryExecutionResultMapper();
            var state = NewState();
            var plan = Plan(state, RetryOperation.Person, RetryOperation.Face);
            var results = new[]
            {
                SuccessResult(),
                new DeviceTaskResult { Success = false, Code = "SDK_ERROR", Retryable = true, Message = "boom" }
            };

            var result = mapper.Map(state, plan, results);

            Assert.False(result.AllSucceeded);
            Assert.Equal(RetryOperation.Face, result.FailedOperation);
            Assert.True(result.Retryable);
            Assert.Equal("SDK_ERROR", result.Code);
            Assert.True(result.SucceededOperations.Contains(RetryOperation.Person));
        }

        [TestCase]
        public static void RetryExecutionResultMapper_AllStepsSucceed_ReportsAllSucceeded()
        {
            var mapper = new RetryExecutionResultMapper();
            var state = NewState();
            var plan = Plan(state, RetryOperation.Person, RetryOperation.Face);
            var results = new[] { SuccessResult(), SuccessResult() };

            var result = mapper.Map(state, plan, results);

            Assert.True(result.AllSucceeded);
            Assert.True(result.SucceededOperations.Contains(RetryOperation.Person));
            Assert.True(result.SucceededOperations.Contains(RetryOperation.Face));
        }

        [TestCase]
        public static void RetryExecutionResultMapper_DeletePersonSuccess_ShortCircuits()
        {
            var mapper = new RetryExecutionResultMapper();
            var state = NewState();
            var plan = Plan(state, RetryOperation.DeletePerson);
            var results = new[] { SuccessResult() };

            var result = mapper.Map(state, plan, results);

            Assert.True(result.AllSucceeded);
            Assert.True(result.SucceededOperations.Contains(RetryOperation.DeletePerson));
            Assert.Equal("OK", result.Code);
        }

        private static DeviceOperationRetryState NewState()
        {
            return new DeviceOperationRetryState
            {
                Id = 1,
                DeviceId = 1,
                EmployeeId = "10001"
            };
        }

        private static RetryCommandPlan Plan(DeviceOperationRetryState state, params RetryOperation[] operations)
        {
            return new RetryCommandPlan(state, operations.Select(item => new RetryOperationStep(item)));
        }

        private static DeviceTaskResult SuccessResult()
        {
            return new DeviceTaskResult { Success = true, Code = "OK" };
        }
    }
}
