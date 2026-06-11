using System;
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
            return Execute(operationName, commandText, command =>
            {
                command.ExecuteScalar();
                return null;
            });
        }

        public DatabaseCommandRecord ExecuteNonQuery(string operationName, string commandText)
        {
            return Execute(operationName, commandText, command => command.ExecuteNonQuery());
        }

        public void Dispose()
        {
            disposed = true;
        }

        private DatabaseCommandRecord Execute(string operationName, string commandText, Func<SqlCommand, int?> execute)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(SqlServerDatabase));
            }

            EnsureReadOnly(commandText);
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
                    connection.Open();
                    record.RowsAffected = execute(command);
                }

                record.ElapsedMs = stopwatch.ElapsedMilliseconds;
                logger?.Info("Database", "数据库只读命令执行成功。", new LogFields
                {
                    OperationName = operationName,
                    ElapsedMs = record.ElapsedMs
                });
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
                if (trimmed.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new InvalidOperationException("阶段 1 禁止执行结构或数据变更 SQL: " + keyword);
                }
            }
        }

        private static bool IsTransient(int number)
        {
            return number == -2 || number == 4060 || number == 10928 || number == 10929 || number == 40197 || number == 40501 || number == 40613;
        }
    }
}
