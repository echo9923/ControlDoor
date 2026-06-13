using System.Collections.Generic;
using System.Linq;
using ControlDoor.Database;

namespace ControlEntradaSalida.Tests
{
    public sealed class RecordingDatabaseClient : IDatabaseClient
    {
        public IList<DatabaseCommandRecord> Commands { get; } = new List<DatabaseCommandRecord>();

        public IList<IReadOnlyDictionary<string, object>> QueryRows { get; } = new List<IReadOnlyDictionary<string, object>>();

        public string FailOperationName { get; set; }

        public int? RowsAffected { get; set; }

        public DatabaseCommandRecord ExecuteScalar(string operationName, string commandText)
        {
            return Record(operationName, commandText);
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText)
        {
            return Record(operationName, commandText);
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            return Record(operationName, commandText, parameters);
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object>> ExecuteQuery(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            Record(operationName, commandText, parameters);
            return QueryRows.ToList();
        }

        public void Dispose()
        {
        }

        private DatabaseCommandRecord Record(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            var record = new DatabaseCommandRecord
            {
                OperationName = operationName,
                CommandText = AppendParameters(commandText, parameters),
                CommandTimeoutSeconds = 30,
                RowsAffected = RowsAffected
            };

            if (operationName == FailOperationName)
            {
                record.Error = new DatabaseError
                {
                    OperationName = operationName,
                    Message = "forced failure"
                };
            }

            Commands.Add(record);
            return record;
        }

        private static string AppendParameters(string commandText, DatabaseParameter[] parameters)
        {
            if (parameters == null || parameters.Length == 0)
            {
                return commandText;
            }

            return commandText + " -- params: " + string.Join(", ", parameters.Select(item => item.Name + "=" + item.Value));
        }
    }
}
