using System;
using System.Collections.Generic;

namespace ControlDoor.Database
{
    public interface IDatabaseClient : IDisposable
    {
        DatabaseCommandRecord ExecuteScalar(string operationName, string commandText);

        DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText);

        DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText, params DatabaseParameter[] parameters);

        IReadOnlyList<IReadOnlyDictionary<string, object>> ExecuteQuery(string operationName, string commandText, params DatabaseParameter[] parameters);
    }
}
