using System;

namespace ControlDoor.Database
{
    public interface ITransactionalDatabaseClient : IDatabaseClient
    {
        void ExecuteTransaction(Action action);
    }
}
