# Targeted Person and Face Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add separate gRPC APIs that batch-sync persons or faces only to an explicitly validated set of ACS devices while preserving the existing all-device `SyncPersons` behavior.

**Architecture:** Extend the existing JSON-string gRPC service and binder with two Unary methods. Parse the top-level `deviceIds` before any side effects, resolve the complete target set atomically from `DeviceRuntimeRegistry`, then reuse existing person and face device-task executors and per-device retry intents without updating global person sync state.

**Tech Stack:** C#/.NET Framework 4.8, Grpc.Core JSON string marshalling, JavaScriptSerializer, repository-local reflection test runner, mock Hikvision gateway.

---

### Task 1: Register the two gRPC contracts

**Files:**
- Modify: `tests/ControlEntradaSalida.Tests/Stage8GrpcCompatibilityDeepTests.cs`
- Modify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcService.cs`
- Modify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcBinder.cs`

- [ ] **Step 1: Write the failing contract test**

Add the two expected Unary methods to the permission method dictionary. The document full-name list remains unchanged until Task 5 updates the contract document:

```csharp
["SyncPersonsToDevices"] = PermissionSyncGrpcService.SyncPersonsToDevicesFullName,
["SyncFacesToDevices"] = PermissionSyncGrpcService.SyncFacesToDevicesFullName
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet run --project tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj -- Stage8GrpcCompatibility
```

Expected: compilation fails because the two full-name constants do not exist.

- [ ] **Step 3: Add the minimal service constants**

Add constants and method-list entries:

```csharp
public const string SyncPersonsToDevicesFullName = "/permission.PermissionSyncService/SyncPersonsToDevices";
public const string SyncFacesToDevicesFullName = "/permission.PermissionSyncService/SyncFacesToDevices";
```


- [ ] **Step 4: Run the focused test and verify GREEN**

Run the same `Stage8GrpcCompatibility` command. Expected: all selected compatibility tests pass.

### Task 2: Implement directed person sync with test-first coverage

**Files:**
- Create: `tests/ControlEntradaSalida.Tests/Stage5TargetedDeviceSyncTests.cs`
- Modify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcService.cs`
- Reuse: `tests/ControlEntradaSalida.Tests/Stage5PermissionSyncTests.cs` fixture types

- [ ] **Step 1: Write failing person behavior tests**

Cover selection, duplicate IDs, offline compensation, no face operation, and no global status write. The primary test shape is:

```csharp
[TestCase]
public static void SyncPersonsToDevices_OnlyCallsSelectedAcsDevices()
{
    using (var fixture = new Stage5Fixture())
    {
        fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
        fixture.AddOnlineDevice(2, new[] { DeviceType.Acs });

        var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
            @"{""deviceIds"":[2],""items"":[{""employee_id"":""0976"",""name"":""张三""}]}",
            fixture.Context("target-person")));

        Assert.Equal("OK", response["code"]);
        Assert.Equal(1, Convert.ToInt32(response["targetDevices"]));
        Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "UpsertPersonAsync"));
        Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UploadFaceAsync"));
        Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
    }
}
```

Add a second test with `deviceIds:[2,2]` asserting one upsert, and an offline test asserting one `SyncPerson` intent for only the selected device.

- [ ] **Step 2: Run the person tests and verify RED**

Run:

```powershell
dotnet run --project tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj -- Stage5TargetedDeviceSyncTests
```

Expected: compilation fails because `SyncPersonsToDevices` does not exist.

- [ ] **Step 3: Implement person request parsing and execution**

Add synchronous and asynchronous entry points using `ExecuteUnary` and `Task.Run`. Parse a JSON object, require `deviceIds`, reject every face-field alias in person items, reuse person field normalization, resolve the complete target list before execution, and call only `ExecutePersonTask`.

For offline or retryable results, call:

```csharp
QueueRetry(device, command.EmployeeId, "SyncPerson",
    PersonPayload(command), null, reason, context)
```

Build the response with `total`, `succeeded`, `failed`, `queued`, `targetDevices`, `queuedDetails`, `items`, `deviceErrors`, and an empty `dbErrors`. Do not call `MarkPersonSynced`.

- [ ] **Step 4: Run the person tests and verify GREEN**

Run the same `Stage5TargetedDeviceSyncTests` command. Expected: all person-directed tests pass.

### Task 3: Implement directed face sync with test-first coverage

**Files:**
- Modify: `tests/ControlEntradaSalida.Tests/Stage5TargetedDeviceSyncTests.cs`
- Modify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcService.cs`
- Modify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcBinder.cs`

- [ ] **Step 1: Write failing face behavior tests**

Add a test proving that only `UploadFaceAsync` is called:

```csharp
[TestCase]
public static void SyncFacesToDevices_UploadsFaceWithoutUpsertingPerson()
{
    using (var fixture = new Stage5Fixture())
    {
        fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });
        var face = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        var response = fixture.Response(fixture.Service.SyncFacesToDevices(
            @"{""deviceIds"":[1],""items"":[{""employee_id"":""0976"",""face_image_base64"":""" + face + @"""}]}",
            fixture.Context("target-face")));

        Assert.Equal("OK", response["code"]);
        Assert.Equal(1, Convert.ToInt32(response["facesUploaded"]));
        Assert.Equal(1, fixture.Gateway.Calls.Count(call => call.MethodName == "UploadFaceAsync"));
        Assert.False(fixture.Gateway.Calls.Any(call => call.MethodName == "UpsertPersonAsync"));
        Assert.Equal(0, fixture.UserWriter.PersonsSynced.Count);
    }
}
```

Add an offline test asserting one `UploadFace` intent for the selected device and no `SyncPerson` intent.

- [ ] **Step 2: Run the face tests and verify RED**

Run the focused test command. Expected: compilation fails because `SyncFacesToDevices` does not exist.

- [ ] **Step 3: Implement face-only parsing and execution**

Require a non-empty face field for every item, decode with the existing 200KB guard, create `PersonSyncCommand` values containing employee ID and face data, and call only `ExecuteUploadFaceTask`. Queue only `UploadFace` retry intents. Include `facesUploaded` in the response.

After both service entry points compile, bind both names with `CreateUnaryMethod`, route their handlers to the corresponding async methods, and extend the contract test with source assertions proving both `.AddMethod` registrations exist.

- [ ] **Step 4: Run the face tests and verify GREEN**

Run the focused test command. Expected: all targeted person and face tests pass.

### Task 4: Enforce atomic validation and regression boundaries

**Files:**
- Modify: `tests/ControlEntradaSalida.Tests/Stage5TargetedDeviceSyncTests.cs`
- Modify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcService.cs`

- [ ] **Step 1: Write failing validation tests**

Add separate tests for missing/empty/non-positive or string-valued `deviceIds`, unknown device, disabled or non-`Acs` device, an empty or over-500 item batch, an invalid person validity range, a person request carrying a face alias, a face request missing a face, invalid Base64, and an oversized face. Each test must assert `INVALID_ARGUMENT`, zero gateway calls, and zero retry intents.

Example:

```csharp
[TestCase]
public static void SyncPersonsToDevices_AnyUnknownDeviceRejectsBeforeAllSideEffects()
{
    using (var fixture = new Stage5Fixture())
    {
        fixture.AddOnlineDevice(1, new[] { DeviceType.Acs });

        var response = fixture.Response(fixture.Service.SyncPersonsToDevices(
            @"{""deviceIds"":[1,999],""items"":[{""employee_id"":""10001""}]}",
            fixture.Context("target-invalid-device")));

        Assert.Equal("INVALID_ARGUMENT", response["code"]);
        Assert.Equal(0, fixture.Gateway.Calls.Count);
        Assert.Equal(0, fixture.RetryWriter.Intents.Count);
    }
}
```

- [ ] **Step 2: Run the validation tests and verify RED**

Run the focused test command. Expected: the new edge tests fail because validation is incomplete.

- [ ] **Step 3: Complete strict target validation**

Implement a helper that accepts only a non-empty JSON array of positive integral IDs, deduplicates in first-seen order, and resolves every snapshot before execution. Reject a snapshot when absent, disabled, or missing declared `DeviceType.Acs`. Finish parsing all records before invoking either device loop.

- [ ] **Step 4: Run focused and legacy Stage 5 tests**

Run:

```powershell
dotnet run --project tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj -- Stage5TargetedDeviceSyncTests
dotnet run --project tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj -- Stage5PermissionSyncTests
```

Expected: both filters pass, proving the new strict APIs and old all-device API coexist.

### Task 5: Document, verify, and commit the implementation

**Files:**
- Modify: `docs/gRPC接口清单.md`
- Modify: `docs/gRPC对接指南.md`
- Verify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcService.cs`
- Verify: `src/ControlDoor/GrpcApi/PermissionSyncGrpcBinder.cs`
- Verify: `tests/ControlEntradaSalida.Tests/Stage5TargetedDeviceSyncTests.cs`
- Verify: `tests/ControlEntradaSalida.Tests/Stage8GrpcCompatibilityDeepTests.cs`

- [ ] **Step 1: Add both documented contracts**

Document full method names, Unary method type, required top-level `deviceIds`/`device_ids`, batch item schemas, validation errors, online/offline behavior, response fields, no implicit person creation for face sync, and no fallback to all devices.

- [ ] **Step 2: Run contract and build checks**

Add both new full names to `Stage8GrpcCompatibility_ContractDocumentListsEveryImplementedFullName`, then run:

```powershell
dotnet run --project tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj -- Stage8GrpcCompatibility
dotnet build src\ControlDoor\ControlDoor.csproj --no-restore --verbosity minimal /p:UseLocalDllReferences=true
```

Expected: compatibility tests and main project build pass with no errors.

- [ ] **Step 3: Run the complete test suite**

Run:

```powershell
dotnet run --project tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj
```

Expected: all discovered tests pass with `Failed: 0`.

- [ ] **Step 4: Inspect scope and commit**

Confirm only the two service files, two test files, two gRPC documents, and implementation-plan tracking changes are included. Leave pre-existing `.vscode/settings.json` and the untracked database script untouched.

Commit with:

```powershell
git commit -m "阶段5，任务定向下发，实现指定设备人员与人脸接口"
```
