using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ControlDoor.Configuration;
using ControlDoor.Database;
using ControlDoor.Devices.Runtime;
using ControlDoor.Hikvision;
using ControlDoor.Observability;
using ControlDoor.Permissions;

namespace ControlEntradaSalida.Tests
{
    public static class Stage6RetryEngineTests
    {
        [TestCase]
        public static void RetryStateMerger_DeletePerson_ClearsConflictingPendingAndResetsAttempt()
        {
            var existing = new DeviceOperationRetryState
            {
                Id = 9,
                DeviceId = 1,
                EmployeeId = "10001",
                PermissionPending = true,
                PermissionLevel = 3,
                PermissionSyncCompletionBlocked = true,
                PersonPending = true,
                PersonPayloadJson = "{}",
                FacePending = true,
                FacePayloadJson = "{}",
                DeleteFacePending = true,
                AttemptCount = 4
            };

            var result = new RetryStateMerger().Merge(existing, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "DeletePerson",
                CreatedAt = new DateTime(2026, 1, 1)
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.True(result.ConflictReset);
            Assert.Equal(0, result.State.AttemptCount);
            Assert.False(result.State.PermissionPending);
            Assert.False(result.State.PermissionSyncCompletionBlocked);
            Assert.False(result.State.PersonPending);
            Assert.False(result.State.FacePending);
            Assert.False(result.State.DeleteFacePending);
            Assert.True(result.State.DeletePersonPending);
            Assert.Equal(null, result.State.PermissionPayloadJson);
            Assert.Equal(null, result.State.PersonPayloadJson);
            Assert.Equal(null, result.State.FacePayloadJson);
        }

        [TestCase]
        public static void RetryStateMerger_TerminalStateNewIntent_ReactivatesAndResetsAttempt()
        {
            var existing = new DeviceOperationRetryState
            {
                Id = 10,
                DeviceId = 1,
                EmployeeId = "10001",
                DeletePersonPending = true,
                AttemptCount = 10,
                ExhaustedAt = new DateTime(2026, 1, 1)
            };

            var result = new RetryStateMerger().Merge(existing, new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "SyncPerson",
                PayloadJson = @"{""employee_id"":""10001"",""name"":""张三""}"
            }, new DateTime(2026, 1, 2), retryImmediatelyOnNewIntent: true);

            Assert.True(result.ReactivatedTerminal);
            Assert.Equal(0, result.State.AttemptCount);
            Assert.Equal(null, result.State.ExhaustedAt);
            Assert.True(result.State.PersonPending);
            Assert.False(result.State.DeletePersonPending);
        }

        [TestCase]
        public static void RetryBackoffCalculator_UsesExponentialDelayWithCap()
        {
            var calculator = new RetryBackoffCalculator(new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 60,
                MaxRetryDelaySeconds = 300
            });

            Assert.Equal(TimeSpan.FromSeconds(60), calculator.CalculateDelay(1));
            Assert.Equal(TimeSpan.FromSeconds(120), calculator.CalculateDelay(2));
            Assert.Equal(TimeSpan.FromSeconds(240), calculator.CalculateDelay(3));
            Assert.Equal(TimeSpan.FromSeconds(300), calculator.CalculateDelay(4));
        }

        [TestCase]
        public static void RetryCommandPlanner_UsesFixedOperationOrder()
        {
            var state = new DeviceOperationRetryState
            {
                DeviceId = 1,
                EmployeeId = "10001",
                PermissionPending = true,
                PersonPending = true,
                FacePending = true,
                DeleteFacePending = true
            };

            var plan = new RetryCommandPlanner().Plan(state);
            var names = plan.Steps.Select(item => item.OperationName).ToList();

            Assert.Equal("DeleteFace", names[0]);
            Assert.Equal("SyncPerson", names[1]);
            Assert.Equal("SyncPermission", names[2]);
            Assert.Equal("UploadFace", names[3]);
        }

        [TestCase]
        public static void RetryCommandPlanner_DeletePersonSuppressesOtherOperations()
        {
            var state = new DeviceOperationRetryState
            {
                DeviceId = 1,
                EmployeeId = "10001",
                PermissionPending = true,
                PersonPending = true,
                FacePending = true,
                DeletePersonPending = true
            };

            var plan = new RetryCommandPlanner().Plan(state);

            Assert.Equal(1, plan.Steps.Count);
            Assert.Equal("DeletePerson", plan.Steps[0].OperationName);
        }

        [TestCase]
        public static void RetryOptionsValidator_InvalidValues_FallBackToStage6Defaults()
        {
            var settings = new AppSettings
            {
                Database = new DatabaseOptions { ConnectionString = "Server=.;Database=test;" },
                DeviceOperationRetry = new DeviceOperationRetryOptions
                {
                    ScanIntervalSeconds = 1,
                    InitialRetryDelaySeconds = 0,
                    MaxRetryDelaySeconds = 1,
                    MaxRetryAttempts = 0,
                    FailureRetentionDays = 0,
                    BatchSize = 0
                }
            };

            var result = new ConfigurationValidator().Validate(settings);

            Assert.True(result.Success);
            Assert.Equal(30, result.Settings.DeviceOperationRetry.ScanIntervalSeconds);
            Assert.Equal(60, result.Settings.DeviceOperationRetry.InitialRetryDelaySeconds);
            Assert.Equal(3600, result.Settings.DeviceOperationRetry.MaxRetryDelaySeconds);
            Assert.Equal(10, result.Settings.DeviceOperationRetry.MaxRetryAttempts);
            Assert.Equal(7, result.Settings.DeviceOperationRetry.FailureRetentionDays);
            Assert.Equal(100, result.Settings.DeviceOperationRetry.BatchSize);
        }

        [TestCase]
        public static void DeviceOperationRetryManager_RunOnce_EmptyScanDoesNotWriteInfoNoise()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.Manager.RunOnceAsync("scan-empty").GetAwaiter().GetResult();

                var text = fixture.ReadLog();
                Assert.False(text.Contains("补偿扫描完成。"));
                Assert.False(text.Contains("ScanRetryStates"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryStore_UpsertIntent_UsesTransactionalUpsertAndMergedState()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { InitialRetryDelaySeconds = 60 });

            var result = store.UpsertIntent(new DeviceOperationRetryIntent
            {
                DeviceId = 1,
                EmployeeId = "10001",
                Operation = "SyncPermission",
                PermissionLevel = 7,
                PayloadJson = @"{""permission_code"":7,""name"":""张三""}",
                LastError = "offline",
                CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0)
            });

            Assert.True(result.Success);
            var command = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.UpsertIntent");
            Assert.Contains("BEGIN TRANSACTION", command.CommandText);
            Assert.Contains("COMMIT TRANSACTION", command.CommandText);
            Assert.Contains("UPDLOCK, HOLDLOCK", command.CommandText);
            Assert.Contains("INSERT INTO dbo.device_operation_retry_states", command.CommandText);
            Assert.Contains("UPDATE dbo.device_operation_retry_states", command.CommandText);
            Assert.Contains("permission_sync_completion_blocked", command.CommandText);
            Assert.Contains("permission_payload", command.CommandText);
            Assert.Contains("@operation=SyncPermission", command.CommandText);
            Assert.Contains("@permissionPayload={\"permission_code\":7,\"name\":\"张三\"}", command.CommandText);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_MarkOperationSuccess_GuardsAgainstStalePayload()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);
            var state = Row(
                id: 8,
                deviceId: 1,
                employeeId: "10001",
                personPending: true,
                personPayload: @"{""employee_id"":""10001"",""name"":""v1""}",
                facePending: true,
                facePayload: @"{""employee_id"":""10001"",""face_image_base64"":""v1""}");

            store.MarkOperationSuccess(DeviceOperationRetryState.FromRow(state), RetryOperation.Person);
            store.MarkOperationSuccess(DeviceOperationRetryState.FromRow(state), RetryOperation.Face);

            var personSql = database.Commands.First(item => item.CommandText.Contains("person_pending = 0")).CommandText;
            var faceSql = database.Commands.First(item => item.CommandText.Contains("face_pending = 0")).CommandText;
            Assert.Contains("person_payload = @personPayload", personSql);
            Assert.Contains("@personPayload={\"employee_id\":\"10001\",\"name\":\"v1\"}", personSql);
            Assert.Contains("face_payload = @facePayload", faceSql);
            Assert.Contains("@facePayload={\"employee_id\":\"10001\",\"face_image_base64\":\"v1\"}", faceSql);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_DeletePersonSuccess_GuardsAgainstConcurrentNewIntents()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database);
            var state = DeviceOperationRetryState.FromRow(Row(
                id: 21,
                deviceId: 1,
                employeeId: "10001",
                deletePersonPending: true));

            store.MarkOperationSuccess(state, RetryOperation.DeletePerson);

            var sql = database.Commands.Single().CommandText;
            Assert.Contains("delete_person_pending = 1", sql);
            Assert.Contains("person_pending = @personPending", sql);
            Assert.Contains("face_pending = @facePending", sql);
            Assert.Contains("permission_pending = @permissionPending", sql);
            Assert.Contains("person_payload = @personPayload", sql);
            Assert.Contains("face_payload = @facePayload", sql);
            Assert.Contains("permission_payload = @permissionPayload", sql);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_PermissionSuccess_UpdatesUserOnlyWhenStateRowChanged()
        {
            var staleDatabase = new RecordingDatabaseClient { RowsAffected = 0 };
            var staleUserWriter = new RecordingUserSyncStatusWriter();
            var staleStore = new DeviceOperationRetryStore(staleDatabase, userSyncWriter: staleUserWriter);
            var state = DeviceOperationRetryState.FromRow(Row(id: 9, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7));

            staleStore.MarkOperationSuccess(state, RetryOperation.Permission);

            var staleSql = staleDatabase.Commands.Single().CommandText;
            Assert.Contains("permission_level = @permissionLevel", staleSql);
            Assert.Contains("permission_payload = @permissionPayload", staleSql);
            Assert.Contains("permission_payload = NULL", staleSql);
            Assert.False(staleUserWriter.PermissionLevels.ContainsKey("10001"));

            var updatedDatabase = new RecordingDatabaseClient { RowsAffected = 1 };
            var updatedUserWriter = new RecordingUserSyncStatusWriter();
            var updatedStore = new DeviceOperationRetryStore(updatedDatabase, userSyncWriter: updatedUserWriter);

            updatedStore.MarkOperationSuccess(state, RetryOperation.Permission);

            Assert.Equal(7, updatedUserWriter.PermissionLevels["10001"]);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_PermissionSuccess_DoesNotUpdateUserWhenEmployeeStillHasBlockingPermissionRows()
        {
            var database = new RecordingDatabaseClient { RowsAffected = 1 };
            database.QueryRowsByOperation["DeviceOperationRetryStore.HasBlockingPermissionStateForEmployee"] =
                new List<IReadOnlyDictionary<string, object>>
                {
                    Row(id: 10, deviceId: 2, employeeId: "10001", permissionPending: true, permissionLevel: 7)
                };
            var userWriter = new RecordingUserSyncStatusWriter();
            var store = new DeviceOperationRetryStore(database, userSyncWriter: userWriter);
            var state = DeviceOperationRetryState.FromRow(Row(id: 9, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7));

            store.MarkOperationSuccess(state, RetryOperation.Permission);

            Assert.False(userWriter.PermissionLevels.ContainsKey("10001"));
            var sql = database.Commands.Last(item => item.OperationName == "DeviceOperationRetryStore.HasBlockingPermissionStateForEmployee").CommandText;
            Assert.Contains("employee_id = @employeeId", sql);
            Assert.Contains("id <> @id", sql);
            Assert.Contains("exhausted_at IS NULL", sql);
            Assert.Contains("permission_pending = 1", sql);
            Assert.Contains("permission_sync_completion_blocked = 1", sql);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_LoadDueStates_UsesDueUnterminalPendingFilter()
        {
            var database = new RecordingDatabaseClient();
            database.QueryRows.Add(Row(id: 1, deviceId: 1, employeeId: "10001", permissionPending: true));
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { BatchSize = 50 });

            var states = store.LoadDueStates(new DateTime(2026, 1, 1));

            Assert.Equal(1, states.Count);
            var sql = database.Commands.Last().CommandText;
            Assert.Contains("exhausted_at IS NULL", sql);
            Assert.Contains("next_retry_at IS NULL OR next_retry_at <= @now", sql);
            Assert.Contains("permission_pending = 1", sql);
            Assert.Contains("@batchSize=50", sql);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_LoadDueStates_UsesDatabaseLocksToAvoidMultiInstanceDuplicateClaims()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { BatchSize = 25 });

            store.LoadDueStates(new DateTime(2026, 1, 1));

            var sql = database.Commands.Last().CommandText;
            Assert.Contains("WITH (UPDLOCK, READPAST, ROWLOCK)", sql);
            Assert.Contains("@batchSize=25", sql);
        }

        [TestCase]
        public static void DeviceOperationRetryStore_LoadDueStates_QueryPassesReadOnlyGuard()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { BatchSize = 25 });

            store.LoadDueStates(new DateTime(2026, 1, 1));

            var sql = database.Commands.Single().CommandText.Split(new[] { " -- params:" }, StringSplitOptions.None)[0];
            SqlServerDatabase.EnsureReadOnly(sql);
        }

        [TestCase]
        public static void DeviceOperationRetryManager_RunLoop_ContinuesAfterScanFailure()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.Options.ScanIntervalSeconds = 1;
                fixture.Database.FailOperationName = "DeviceOperationRetryStore.LoadDueStates";
                fixture.Database.ThrowOnFailure = true;

                fixture.Manager.StartAsync(new ControlDoor.Runtime.BackgroundTaskContext("stage6-loop", System.Threading.CancellationToken.None, null)).GetAwaiter().GetResult();
                WaitUntil(() => fixture.Database.Commands.Count(command => command.OperationName == "DeviceOperationRetryStore.LoadDueStates") >= 2, "retry manager did not run a second scan after the first scan failed.");
                var status = fixture.Manager.GetStatus();

                Assert.True(status.IsRunning);
            }
        }

        [TestCase]
        public static void DeviceOperationRetryStore_TryClaimDueState_UsesGuardedUpdateToLeaseExistingRow()
        {
            var database = new RecordingDatabaseClient { RowsAffected = 1 };
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { InitialRetryDelaySeconds = 60 });
            var state = DeviceOperationRetryState.FromRow(Row(id: 12, deviceId: 1, employeeId: "10001", permissionPending: true));

            var claimed = store.TryClaimDueState(state, new DateTime(2026, 1, 1, 10, 0, 0));

            Assert.True(claimed);
            var sql = database.Commands.Single(item => item.OperationName == "DeviceOperationRetryStore.TryClaimDueState").CommandText;
            Assert.Contains("UPDATE dbo.device_operation_retry_states", sql);
            Assert.Contains("next_retry_at = @claimUntil", sql);
            Assert.Contains("last_attempt_at = @now", sql);
            Assert.Contains("AND (next_retry_at IS NULL OR next_retry_at <= @now)", sql);
            Assert.Contains("AND exhausted_at IS NULL", sql);
            Assert.Contains("@claimUntil=2026/1/1 10:01:00", sql);
        }

        [TestCase]
        public static void DeviceOperationRetryManager_DatabaseClaimMiss_DoesNotSubmitDuplicateRetryTask()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Database.RowsAffected = 0;
                fixture.Database.QueryRows.Add(Row(id: 13, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7));

                var result = fixture.Manager.RunOnceAsync("stage6-claim-miss").GetAwaiter().GetResult();

                Assert.Equal(1, result.Due);
                Assert.Equal(1, result.ClaimSkipped);
                Assert.Equal(0, result.Submitted);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.TryClaimDueState"));
                Assert.False(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_OnlinePermission_SubmitsRetryTaskAndDeletesCompletedState()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice(description: "办公区域");
                fixture.Database.QueryRows.Add(Row(id: 1, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 1));

                var result = fixture.Manager.RunOnceAsync("stage6-online").GetAwaiter().GetResult();

                Assert.Equal(1, result.Due);
                Assert.Equal(1, result.Submitted);
                Assert.Equal(1, result.Succeeded);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
                var request = (UpsertPersonRequest)fixture.Gateway.Calls.Last(call => call.MethodName == "UpsertPersonAsync").Request;
                Assert.Equal("10001", request.Person.EmployeeId);
                Assert.Equal("10001", request.Person.Name);
                Assert.True(request.Person.Enabled);
                Assert.Equal(PersonProvisioningMode.Permission, request.ProvisioningMode);
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess"));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_ProductionPermissionLevelOne_DisablesPerson()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice(description: "生产区域");
                fixture.Database.QueryRows.Add(Row(id: 10, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 1));

                var result = fixture.Manager.RunOnceAsync("stage6-production-level1").GetAwaiter().GetResult();

                Assert.Equal(1, result.Succeeded);
                var request = (UpsertPersonRequest)fixture.Gateway.Calls.Last(call => call.MethodName == "UpsertPersonAsync").Request;
                Assert.False(request.Person.Enabled);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_PermissionPayloadName_IsUsedWhenPresent()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice(description: "办公区域");
                fixture.Database.QueryRows.Add(Row(
                    id: 11,
                    deviceId: 1,
                    employeeId: "10001",
                    permissionPending: true,
                    permissionLevel: 1,
                    permissionPayload: @"{""employee_id"":""10001"",""name"":""张三"",""permission_code"":1}"));

                var result = fixture.Manager.RunOnceAsync("stage6-permission-name").GetAwaiter().GetResult();

                Assert.Equal(1, result.Succeeded);
                var request = (UpsertPersonRequest)fixture.Gateway.Calls.Last(call => call.MethodName == "UpsertPersonAsync").Request;
                Assert.Equal("10001", request.Person.EmployeeId);
                Assert.Equal("张三", request.Person.Name);
                Assert.True(request.Person.Enabled);
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_OfflineDevice_DefersWithoutSubmittingTask()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOfflineDevice();
                fixture.Database.QueryRows.Add(Row(id: 2, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7));

                var result = fixture.Manager.RunOnceAsync("stage6-offline").GetAwaiter().GetResult();

                Assert.Equal(1, result.OfflineDeferred);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeferOffline"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_PersonSuccessFaceFailure_ClearsPersonAndSchedulesFaceRetry()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UploadFaceAsync");
                fixture.Database.QueryRows.Add(Row(
                    id: 3,
                    deviceId: 1,
                    employeeId: "10001",
                    personPending: true,
                    personPayload: @"{""employee_id"":""10001"",""name"":""张三""}",
                    facePending: true,
                    facePayload: @"{""employee_id"":""10001"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}"));

                var result = fixture.Manager.RunOnceAsync("stage6-partial").GetAwaiter().GetResult();

                Assert.Equal(1, result.Submitted);
                Assert.Equal(1, result.Failed);
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
                Assert.True(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkOperationSuccess" && item.CommandText.Contains("person_pending = 0")));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_DeletePersonSuccess_DeletesStateWithoutOtherOperations()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Database.QueryRows.Add(Row(
                    id: 4,
                    deviceId: 1,
                    employeeId: "10001",
                    deletePersonPending: true,
                    permissionPending: true,
                    personPending: true,
                    facePending: true));

                var result = fixture.Manager.RunOnceAsync("stage6-delete-person").GetAwaiter().GetResult();

                Assert.Equal(1, result.Succeeded);
                var calls = fixture.Gateway.Calls.ToList();
                var faceIndex = calls.FindIndex(call => call.MethodName == "DeleteFaceAsync");
                var personIndex = calls.FindIndex(call => call.MethodName == "DeletePersonAsync");
                Assert.True(faceIndex >= 0);
                Assert.True(personIndex > faceIndex);
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "SetPermissionAsync"));
                Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
                Assert.True(fixture.Database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.DeleteIfCompleted"));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_DeviceDisabled_MarksTerminalFailure()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddDisabledDevice();
                fixture.Database.QueryRows.Add(Row(id: 5, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7));

                var result = fixture.Manager.RunOnceAsync("stage6-disabled").GetAwaiter().GetResult();

                Assert.Equal(1, result.Terminal);
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure" &&
                    item.CommandText.Contains("@lastError=DEVICE_DISABLED")));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryManager_RetryableFailureAtMaxAttempts_MarksTerminal()
        {
            using (var fixture = new Stage6Fixture())
            {
                fixture.AddOnlineDevice();
                fixture.Gateway.ConfigureTimeout("UploadFaceAsync");
                fixture.Database.QueryRows.Add(Row(
                    id: 6,
                    deviceId: 1,
                    employeeId: "10001",
                    facePending: true,
                    facePayload: @"{""employee_id"":""10001"",""face_image_base64"":""" + Stage5TestData.JpegBase64() + @"""}",
                    attemptCount: 2));

                var result = fixture.Manager.RunOnceAsync("stage6-exhausted").GetAwaiter().GetResult();

                Assert.Equal(1, result.Terminal);
                Assert.Equal(0, result.Failed);
                Assert.True(fixture.Database.Commands.Any(item =>
                    item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure" &&
                    item.CommandText.Contains("@lastError=RETRY_EXHAUSTED")));
            }
        }

        [TestCase]
        public static void DeviceOperationRetryStore_QueueFullFailure_SchedulesRetryWithoutTerminal()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions
            {
                InitialRetryDelaySeconds = 5,
                MaxRetryDelaySeconds = 30,
                MaxRetryAttempts = 3
            });
            var state = DeviceOperationRetryState.FromRow(Row(id: 7, deviceId: 1, employeeId: "10001", permissionPending: true, permissionLevel: 7));
            var result = new RetryExecutionResult(
                state,
                Enumerable.Empty<RetryOperation>(),
                RetryOperation.Permission,
                false,
                "QUEUE_FULL",
                "Worker queue is full.",
                null);

            store.ApplyExecutionResult(result, new DateTime(2026, 1, 1, 10, 0, 0));

            Assert.True(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.ScheduleRetry"));
            Assert.False(database.Commands.Any(item => item.OperationName == "DeviceOperationRetryStore.MarkTerminalFailure"));
        }

        [TestCase]
        public static void DeviceOperationRetryStore_CleanupExpiredFailures_DeletesOnlyTerminalRows()
        {
            var database = new RecordingDatabaseClient();
            var store = new DeviceOperationRetryStore(database, new DeviceOperationRetryOptions { FailureRetentionDays = 7, BatchSize = 12 });

            store.CleanupExpiredFailures(new DateTime(2026, 1, 10), 12);

            var sql = database.Commands.Last().CommandText;
            Assert.Contains("exhausted_at IS NOT NULL", sql);
            Assert.Contains("exhausted_at < @cutoff", sql);
            Assert.Contains("TOP (@batchSize)", sql);
            Assert.Contains("@batchSize=12", sql);
        }

        private static IReadOnlyDictionary<string, object> Row(
            long id,
            int deviceId,
            string employeeId,
            int? permissionLevel = null,
            string permissionPayload = null,
            bool permissionPending = false,
            bool personPending = false,
            string personPayload = null,
            bool facePending = false,
            string facePayload = null,
            bool deletePersonPending = false,
            bool deleteFacePending = false,
            int attemptCount = 0)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = id,
                ["device_id"] = deviceId,
                ["employee_id"] = employeeId,
                ["permission_level"] = permissionLevel,
                ["permission_payload"] = permissionPayload,
                ["permission_pending"] = permissionPending,
                ["permission_sync_completion_blocked"] = permissionPending,
                ["person_payload"] = personPayload,
                ["person_pending"] = personPending,
                ["face_payload"] = facePayload,
                ["face_pending"] = facePending,
                ["delete_person_pending"] = deletePersonPending,
                ["delete_face_pending"] = deleteFacePending,
                ["attempt_count"] = attemptCount,
                ["next_retry_at"] = new DateTime(2026, 1, 1),
                ["last_error"] = null,
                ["last_attempt_at"] = null,
                ["exhausted_at"] = null,
                ["created_at"] = new DateTime(2026, 1, 1),
                ["updated_at"] = new DateTime(2026, 1, 1)
            };
        }

        private static void WaitUntil(System.Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(50);
            }

            Assert.True(condition(), message);
        }
    }

    internal sealed class Stage6Fixture : IDisposable
    {
        private readonly string runDirectory = TestWorkspace.Create();
        private readonly ServiceLogger logger;
        private readonly Stage4Fixture inner;

        public Stage6Fixture()
        {
            logger = new ServiceLogger(LogOptions.FromSettings(runDirectory, new LoggingOptions { LogDirectory = "logs" }));
            inner = new Stage4Fixture(logger);
            Database = new RecordingDatabaseClient();
            Options = new DeviceOperationRetryOptions
            {
                ScanIntervalSeconds = 30,
                InitialRetryDelaySeconds = 1,
                MaxRetryDelaySeconds = 5,
                MaxRetryAttempts = 3,
                FailureRetentionDays = 7,
                BatchSize = 100
            };
            Store = new DeviceOperationRetryStore(Database, Options);
            Manager = new DeviceOperationRetryManager(
                Store,
                inner.Registry,
                new RetryExecutionCoordinator(inner.Dispatcher, inner.Gateway, logger),
                Options,
                logger);
        }

        public RecordingDatabaseClient Database { get; }

        public DeviceOperationRetryOptions Options { get; }

        public DeviceOperationRetryStore Store { get; }

        public DeviceOperationRetryManager Manager { get; }

        public MockHikvisionGateway Gateway => inner.Gateway;

        public string ReadLog()
        {
            return System.IO.File.Exists(logger.CurrentLogPath)
                ? System.IO.File.ReadAllText(logger.CurrentLogPath)
                : string.Empty;
        }

        public void AddOnlineDevice(string description = "测试设备")
        {
            inner.AddRecord(1, description: description);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
            var login = inner.Lifecycle.SubmitLogin(1, wait: true, requestId: "stage6-login");
            Assert.True(login.Success, login.Message);
            inner.Registry.UpdateCapabilities(1, new ControlDoor.Devices.Runtime.DeviceCapabilities
            {
                Known = true,
                SupportsFaceConfig = true,
                SupportsPersonConfig = true,
                SupportsAcs = true
            }, DateTime.Now);
        }

        public void AddOfflineDevice(string description = "测试设备")
        {
            inner.AddRecord(1, description: description);
            inner.Lifecycle.LoadEnabledDevices(enqueueLogin: false);
        }

        public void AddDisabledDevice()
        {
            inner.Registry.Register(new DeviceRuntimeCreationOptions
            {
                DeviceId = 1,
                DeviceName = "停用设备",
                IpAddress = "192.168.1.65",
                Port = 8000,
                Username = "admin",
                Password = "12345",
                Enabled = false,
                CreatedAt = DateTime.Now
            });
        }

        public void Dispose()
        {
            inner.Dispose();
            logger.Dispose();
        }
    }
}
