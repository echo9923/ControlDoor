using System;

namespace ControlDoor.Database
{
    public interface IDatabaseClient : IDisposable
    {
        DatabaseCommandRecord ExecuteScalar(string operationName, string commandText);

        DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText);
    }
}
