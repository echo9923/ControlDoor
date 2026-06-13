using System.Collections.Generic;

namespace ControlDoor.Permissions
{
    public sealed class RetryCommandPlanner
    {
        public RetryCommandPlan Plan(DeviceOperationRetryState state)
        {
            var steps = new List<RetryOperationStep>();
            if (state == null || !state.HasPending)
            {
                return new RetryCommandPlan(state, steps);
            }

            if (state.DeletePersonPending)
            {
                steps.Add(new RetryOperationStep(RetryOperation.DeletePerson));
                return new RetryCommandPlan(state, steps);
            }

            if (state.DeleteFacePending)
            {
                steps.Add(new RetryOperationStep(RetryOperation.DeleteFace));
            }

            if (state.PersonPending)
            {
                steps.Add(new RetryOperationStep(RetryOperation.Person));
            }

            if (state.PermissionPending)
            {
                steps.Add(new RetryOperationStep(RetryOperation.Permission));
            }

            if (state.FacePending)
            {
                steps.Add(new RetryOperationStep(RetryOperation.Face));
            }

            return new RetryCommandPlan(state, steps);
        }
    }
}
