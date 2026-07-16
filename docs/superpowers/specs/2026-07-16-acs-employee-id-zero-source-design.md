# Source-Aware ACS Employee ID Zero Handling

## Problem

`NET_DVR_ACS_EVENT_INFO.dwEmployeeNo` is a numeric compatibility field. When the device does not provide an employee number through that field, its default value is `0`. The current callback converts the default to string `"0"`; downstream code accepts any non-empty string and persists it as a real employee ID.

An explicit `"0"` from string fields such as `employeeId` or `byEmployeeNo` can still be a valid business identifier and must remain valid.

## Confirmed Semantics

- Explicit `"0"` from `employeeId`, `byEmployeeNo`, or `EmployeeId` is valid and preserved.
- Numeric-source `dwEmployeeNo = 0` means that source did not provide an employee ID and must not be promoted.
- Positive `dwEmployeeNo` values remain supported for older devices.
- If no valid string or numeric employee ID exists, the existing card-number fallback applies.
- If both employee ID and card number are missing, parsing rejects the event.
- Raw `dwEmployeeNo` remains in the payload for diagnostics, including raw value `"0"`.

## Design

Apply defense at both data boundaries:

1. `HikvisionSdkWrapper.ApplyAcsAlarmInfo` promotes `dwEmployeeNo` only when it is positive, while always retaining `Values["dwEmployeeNo"]`.
2. `AcsEventParser` preserves string-source semantics and accepts key `dwEmployeeNo` only when it parses as a positive integer. It returns the original string so leading zeros are not normalized away.

The existing `byEmployeeNo` precedence remains unchanged, preserving IDs such as `"0976"`, `"000976"`, and explicit `"0"`.

## Regression Matrix

- Base SDK event with `dwEmployeeNo = 0`: `EmployeeId` stays empty; raw numeric value stays `"0"`.
- Extended SDK event with `byEmployeeNo = "0"`: final employee ID is explicit string `"0"`.
- Parser with only `dwEmployeeNo = "0"`: returns `MISSING_PERSON_KEY`.
- Parser with a negative or non-numeric `dwEmployeeNo`: treats that numeric source as missing.
- Parser with `dwEmployeeNo = "0"` and a card number: uses the card number.
- Parser preserves explicit string `"0"`, leading-zero IDs, and positive numeric compatibility values.

## Non-Goals

- No database schema or SQL parameter changes.
- No guessing or reconstructing lost leading zeros.
- No general numeric validation or numeric conversion of business employee IDs.
