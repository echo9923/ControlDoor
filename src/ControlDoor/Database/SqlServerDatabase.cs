using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using ControlDoor.Configuration;
using ControlDoor.Observability;

namespace ControlDoor.Database
{
    public sealed class SqlServerDatabase : IDatabaseClient
    {
        private readonly DatabaseOptions options;
        private readonly ServiceLogger logger;
        private bool disposed;

        public SqlServerDatabase(DatabaseOptions options, ServiceLogger logger = null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger;
        }

        public DatabaseCommandRecord ExecuteScalar(string operationName, string commandText)
        {
            EnsureReadOnly(commandText);
            return Execute(operationName, commandText, null, command =>
            {
                command.ExecuteScalar();
                return null;
            });
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText)
        {
            EnsureReadOnly(commandText);
            return Execute(operationName, commandText, null, command => command.ExecuteNonQuery());
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            return Execute(operationName, commandText, parameters, command => command.ExecuteNonQuery());
        }

        public IReadOnlyList<IReadOnlyDictionary<string, object>> ExecuteQuery(string operationName, string commandText, params DatabaseParameter[] parameters)
        {
            EnsureReadOnly(commandText);
            var rows = new List<IReadOnlyDictionary<string, object>>();
            var record = Execute(operationName, commandText, parameters, command =>
            {
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        for (var index = 0; index < reader.FieldCount; index++)
                        {
                            row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                        }

                        rows.Add(row);
                    }
                }

                return rows.Count;
            });

            if (record.Error != null)
            {
                throw new InvalidOperationException(record.Error.Message);
            }

            return rows;
        }

        public void Dispose()
        {
            disposed = true;
        }

        private DatabaseCommandRecord Execute(string operationName, string commandText, DatabaseParameter[] parameters, Func<SqlCommand, int?> execute)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(SqlServerDatabase));
            }

            var stopwatch = Stopwatch.StartNew();
            var record = new DatabaseCommandRecord
            {
                OperationName = operationName,
                CommandText = commandText,
                CommandTimeoutSeconds = options.CommandTimeoutSeconds
            };

            try
            {
                using (var connection = new SqlConnection(options.ConnectionString))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = commandText;
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = options.CommandTimeoutSeconds;
                    AddParameters(command, parameters);
                    connection.Open();
                    record.RowsAffected = execute(command);
                }

                record.ElapsedMs = stopwatch.ElapsedMilliseconds;
                if (logger == null || !logger.IsSlowOperation(record.ElapsedMs))
                {
                    logger?.Debug("Database", "数据库只读命令执行成功。", new LogFields
                    {
                        OperationName = operationName,
                        ElapsedMs = record.ElapsedMs
                    });
                }
                else
                {
                    logger.Warn("Database", "数据库命令执行较慢。", new LogFields
                    {
                        OperationName = operationName,
                        ElapsedMs = record.ElapsedMs,
                        Extra = { ["thresholdMs"] = logger.SlowOperationThresholdMs.ToString() }
                    });
                }
            }
            catch (SqlException ex)
            {
                record.ElapsedMs = stopwatch.ElapsedMilliseconds;
                record.Error = new DatabaseError
                {
                    OperationName = operationName,
                    SqlErrorNumber = ex.Number,
                    Message = ex.Message,
                    ExceptionType = ex.GetType().Name,
                    ElapsedMs = record.ElapsedMs,
                    CanRetry = IsTransient(ex.Number)
                };
                logger?.Error("Database", "数据库命令执行失败。", ex, new LogFields
                {
                    OperationName = operationName,
                    ElapsedMs = record.ElapsedMs,
                    ErrorCode = ex.Number.ToString()
                });
            }
            catch (Exception ex)
            {
                record.ElapsedMs = stopwatch.ElapsedMilliseconds;
                record.Error = new DatabaseError
                {
                    OperationName = operationName,
                    Message = ex.Message,
                    ExceptionType = ex.GetType().Name,
                    ElapsedMs = record.ElapsedMs,
                    CanRetry = false
                };
                logger?.Error("Database", "数据库命令执行失败。", ex, new LogFields
                {
                    OperationName = operationName,
                    ElapsedMs = record.ElapsedMs
                });
            }

            return record;
        }

        private static void AddParameters(SqlCommand command, DatabaseParameter[] parameters)
        {
            if (parameters == null)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.Name))
                {
                    continue;
                }

                var name = parameter.Name.StartsWith("@", StringComparison.Ordinal)
                    ? parameter.Name
                    : "@" + parameter.Name;
                command.Parameters.AddWithValue(name, parameter.Value ?? DBNull.Value);
            }
        }

        public static void EnsureReadOnly(string commandText)
        {
            if (string.IsNullOrWhiteSpace(commandText))
            {
                throw new InvalidOperationException("SQL 不能为空。");
            }

            var trimmed = commandText.TrimStart();
            if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("阶段 1 数据库健康检查只允许 SELECT 只读语句。");
            }

            var forbidden = new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "ALTER", "CREATE", "DROP", "TRUNCATE", "EXEC", "EXECUTE" };
            foreach (var keyword in forbidden)
            {
                if (ContainsSqlKeyword(trimmed, keyword))
                {
                    throw new InvalidOperationException("阶段 1 禁止执行结构或数据变更 SQL: " + keyword);
                }
            }
        }

        private static bool ContainsSqlKeyword(string commandText, string keyword)
        {
            var index = 0;
            while (index < commandText.Length)
            {
                var found = commandText.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return false;
                }

                var before = found == 0 ? '\0' : commandText[found - 1];
                var afterIndex = found + keyword.Length;
                var after = afterIndex >= commandText.Length ? '\0' : commandText[afterIndex];
                if (!IsIdentifierCharacter(before) && !IsIdentifierCharacter(after))
                {
                    return true;
                }

                index = found + keyword.Length;
            }

            return false;
        }

        private static bool IsIdentifierCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static bool IsTransient(int number)
        {
            return number == -2 || number == 4060 || number == 10928 || number == 10929 || number == 40197 || number == 40501 || number == 40613;
        }
    }
}
