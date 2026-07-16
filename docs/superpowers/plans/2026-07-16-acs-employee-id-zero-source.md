# ACS Employee ID Zero Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent SDK numeric default `dwEmployeeNo = 0` from being persisted as employee ID `"0"`, while preserving explicitly reported string employee ID `"0"`.

**Architecture:** Keep source semantics at both boundaries. The SDK wrapper promotes only positive legacy numeric employee numbers but retains the raw numeric value; the ACS parser accepts key `dwEmployeeNo` only as a positive integer while returning the original string, preserving string sources, leading zeros, and card fallback.

**Tech Stack:** C#, .NET Framework 4.8, reflection-based `[TestCase]` runner, Hikvision callback marshalling.

---

### Task 1: Add Parser Regression Tests

**Files:**
- Modify: `tests/ControlEntradaSalida.Tests/Stage7AcsEventParserEdgeTests.cs:89-153`
- Test: `tests/ControlEntradaSalida.Tests/Stage7AcsEventParserEdgeTests.cs`

- [ ] **Step 1: Replace the ambiguous numeric-zero test**

Add tests asserting that raw `dwEmployeeNo = "0"` without a card returns `MISSING_PERSON_KEY`, the same value with `cardNo = "CARD-10001"` uses that card, and explicit `byEmployeeNo = "0"` remains employee ID `"0"`.

- [ ] **Step 2: Build and verify RED**

```powershell
dotnet build tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj --no-restore --verbosity minimal -p:UseLocalDllReferences=true
tests\ControlEntradaSalida.Tests\bin\Debug\net48\ControlEntradaSalida.Tests.exe Stage7AcsEventParserEdgeTests
```

Expected: the numeric-default tests fail because the parser currently returns employee ID `"0"`.

### Task 2: Add SDK Callback Regression Tests

**Files:**
- Modify: `tests/ControlEntradaSalida.Tests/Stage3GatewayTests.cs:791-828`
- Modify: `tests/ControlEntradaSalida.Tests/Stage3GatewayTests.cs:1493-1583`
- Test: `tests/ControlEntradaSalida.Tests/Stage3GatewayTests.cs`

- [ ] **Step 1: Add native callback scenarios**

Add one test that emits a base ACS structure with `dwEmployeeNo = 0`, no extension, and card `CARD-10001`; assert `EmployeeId` is empty, `CardNumber` is preserved, and raw `dwEmployeeNo` is `"0"`. Add another test that emits `byEmployeeNo = "0"` with numeric zero and asserts explicit string `"0"` is preserved.

- [ ] **Step 2: Build and verify RED**

```powershell
dotnet build tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj --no-restore --verbosity minimal -p:UseLocalDllReferences=true
tests\ControlEntradaSalida.Tests\bin\Debug\net48\ControlEntradaSalida.Tests.exe SdkWrapper_NativeAcsAlarm_DefaultNumericEmployeeNoIsNotPromoted
```

Expected: failure because the wrapper currently promotes numeric zero to `EmployeeId = "0"`.

### Task 3: Implement Source-Aware Resolution

**Files:**
- Modify: `src/ControlDoor/Hikvision/HikvisionSdkWrapper.cs:751-760`
- Modify: `src/ControlDoor/FaceEvents/AcsEventParser.cs:28-32`
- Modify: `src/ControlDoor/FaceEvents/AcsEventParser.cs:126-142`

- [ ] **Step 1: Guard numeric promotion at the callback boundary**

```csharp
data.CardNumber = GetAnsiString(eventInfo.byCardNo);
if (eventInfo.dwEmployeeNo > 0)
{
    data.EmployeeId = eventInfo.dwEmployeeNo.ToString();
}
```

Keep `data.Values["dwEmployeeNo"] = eventInfo.dwEmployeeNo.ToString();` unchanged for diagnostics.

- [ ] **Step 2: Guard the raw numeric source in the parser**

```csharp
var employeeId = FirstNonEmpty(
    GetValue(rawEvent, "employeeId"),
    GetValue(rawEvent, "byEmployeeNo"),
    GetValue(rawEvent, "EmployeeId"),
    PositiveSdkEmployeeNo(rawEvent));

private static string PositiveSdkEmployeeNo(RawAcsAlarmEvent rawEvent)
{
    var value = GetValue(rawEvent, "dwEmployeeNo");
    int numericValue;
    return int.TryParse(value, out numericValue) && numericValue > 0 ? value : null;
}
```

- [ ] **Step 3: Build and verify GREEN**

```powershell
dotnet build tests\ControlEntradaSalida.Tests\ControlEntradaSalida.Tests.csproj --no-restore --verbosity minimal -p:UseLocalDllReferences=true
tests\ControlEntradaSalida.Tests\bin\Debug\net48\ControlEntradaSalida.Tests.exe Stage7AcsEventParserEdgeTests
tests\ControlEntradaSalida.Tests\bin\Debug\net48\ControlEntradaSalida.Tests.exe SdkWrapper_NativeAcsAlarm
```

Expected: all selected tests pass.

### Task 4: Full Verification and Commit

**Files:**
- Verify all files listed above.

- [ ] **Step 1: Run the complete test executable**

```powershell
tests\ControlEntradaSalida.Tests\bin\Debug\net48\ControlEntradaSalida.Tests.exe
```

Expected: `Failed: 0`.

- [ ] **Step 2: Run the repository package workflow**

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish-package.ps1
powershell -ExecutionPolicy Bypass -File tools\test-service-package.ps1 -PackageRoot '门禁publish\ServicePackage'
```

Expected: package build succeeds and validation reports `Service package check passed`.

- [ ] **Step 3: Inspect the diff**

```powershell
git -c safe.directory='D:/codeproject/c#/ControlDoor' diff --check
git -c safe.directory='D:/codeproject/c#/ControlDoor' diff --stat
```

Expected: no whitespace errors and only approved documentation, SDK wrapper, parser, and regression tests changed.

- [ ] **Step 4: Commit**

```powershell
git add AGENTS.md docs/superpowers/specs/2026-07-16-acs-employee-id-zero-source-design.md docs/superpowers/plans/2026-07-16-acs-employee-id-zero-source.md src/ControlDoor/Hikvision/HikvisionSdkWrapper.cs src/ControlDoor/FaceEvents/AcsEventParser.cs tests/ControlEntradaSalida.Tests/Stage3GatewayTests.cs tests/ControlEntradaSalida.Tests/Stage7AcsEventParserEdgeTests.cs
git commit -m "阶段7，任务03，修复默认工号零误入库"
```
