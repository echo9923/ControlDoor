using System.Collections.Generic;
using ControlDoor.Database;

namespace ControlEntradaSalida.Tests
{
    public sealed class RecordingDatabaseClient : IDatabaseClient
    {
        public IList<DatabaseCommandRecord> Commands { get; } = new List<DatabaseCommandRecord>();

        public string FailOperationName { get; set; }

        public DatabaseCommandRecord ExecuteScalar(string operationName, string commandText)
        {
            return Record(operationName, commandText);
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText)
        {
            return Record(operationName, commandText);
        }

        public void Dispose()
        {
        }

        private DatabaseCommandRecord Record(string operationName, string commandText)
        {
            var record = new DatabaseCommandRecord
            {
                OperationName = operationName,
                CommandText = commandText,
                CommandTimeoutSeconds = 30
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
    }
}
