using System;
using System.Collections.Generic;

namespace ControlDoor.Database
{
    public sealed class ReadOnlyDatabaseClient : IDatabaseClient
    {
        private readonly IDatabaseClient inner;

        public ReadOnlyDatabaseClient(IDatabaseClient inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public DatabaseCommandRecord ExecuteScalar(string operationName, string commandText)
        {
            SqlServerDatabase.EnsureReadOnly(commandText);
            return inner.ExecuteScalar(operationName, commandText);
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText)
        {
            SqlServerDatabase.EnsureReadOnly(commandText);
            return inner.ExecuteNonQuery(operationName, commandText);
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            SqlServerDatabase.EnsureReadOnly(commandText);
            return inner.ExecuteNonQuery(operationName, commandText, parameters);
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object>> ExecuteQuery(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            SqlServerDatabase.EnsureReadOnly(commandText);
            return inner.ExecuteQuery(operationName, commandText, parameters);
        }

        public void Dispose()
        {
        }
    }
}
