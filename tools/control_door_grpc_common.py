import argparse
import base64
import getpass
import json
import os
import socket
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple


DEFAULT_HOST = "localhost:5001"
DEFAULT_DEVICE_ID = 11
DEFAULT_DEVICE_IP = "169.254.66.109"
DEFAULT_DEVICE_PORT = 8000
DEFAULT_USERNAME = "admin"
DEFAULT_PERMISSION_CODE = 1
DEFAULT_WAIT_SECONDS = 120
DEFAULT_REPORT_DIR = Path("artifacts") / "real-device-grpc"
TEST_EMPLOYEE_PREFIX = "TEST_CD_"
PASSWORD_ENV = "CONTROLDOOR_DEVICE_PASSWORD"
API_KEY_ENV = "CONTROLDOOR_GRPC_API_KEY"

ACCESS_SERVICE = "device.AccessControlService"
PERMISSION_SERVICE = "permission.PermissionSyncService"

METHODS = {
    "GetDeviceStatus": (ACCESS_SERVICE, "GetDeviceStatus", "unary"),
    "AddDevice": (ACCESS_SERVICE, "AddDevice", "unary"),
    "DeleteDevice": (ACCESS_SERVICE, "DeleteDevice", "unary"),
    "DisconnectDevice": (ACCESS_SERVICE, "DisconnectDevice", "unary"),
    "ReconnectDevice": (ACCESS_SERVICE, "ReconnectDevice", "unary"),
    "SyncPermissions": (PERMISSION_SERVICE, "SyncPermissions", "unary"),
    "SyncPersons": (PERMISSION_SERVICE, "SyncPersons", "unary"),
    "DeleteFaces": (PERMISSION_SERVICE, "DeleteFaces", "unary"),
    "DeletePersons": (PERMISSION_SERVICE, "DeletePersons", "unary"),
    "GetFaces": (PERMISSION_SERVICE, "GetFaces", "unary"),
    "CaptureFaceStream": (PERMISSION_SERVICE, "CaptureFaceStream", "stream"),
    "GetEnrollmentStatus": (PERMISSION_SERVICE, "GetEnrollmentStatus", "unary"),
}


def iso_now() -> str:
    return datetime.now().isoformat(timespec="seconds")


class MissingGrpcDependency(RuntimeError):
    pass


class SmokeFailure(RuntimeError):
    pass


@dataclass
class TestConfig:
    host: str = DEFAULT_HOST
    device_id: int = DEFAULT_DEVICE_ID
    device_ip: str = DEFAULT_DEVICE_IP
    device_port: int = DEFAULT_DEVICE_PORT
    username: str = DEFAULT_USERNAME
    password: str = ""
    api_key: str = ""
    employee_id: str = ""
    face_image: str = ""
    permission_code: int = DEFAULT_PERMISSION_CODE
    wait_seconds: int = DEFAULT_WAIT_SECONDS
    timeout_seconds: int = 60
    cleanup: bool = False
    delete_device_record: bool = False
    report: str = ""
    full: bool = False

    def ensure_employee_id(self) -> None:
        if not self.employee_id:
            self.employee_id = TEST_EMPLOYEE_PREFIX + datetime.now().strftime("%Y%m%d_%H%M%S")

    @property
    def can_auto_cleanup_employee(self) -> bool:
        return self.employee_id.upper().startswith(TEST_EMPLOYEE_PREFIX)


@dataclass
class StepRecord:
    name: str
    request: Any
    response: Any = None
    raw_response: Any = None
    started_at: str = ""
    elapsed_ms: int = 0
    success: bool = False
    code: str = ""
    message: str = ""
    error: str = ""
    classification: str = ""

    def to_dict(self) -> Dict[str, Any]:
        return {
            "name": self.name,
            "request": self.request,
            "response": self.response,
            "rawResponse": self.raw_response,
            "startedAt": self.started_at,
            "elapsedMs": self.elapsed_ms,
            "success": self.success,
            "code": self.code,
            "message": self.message,
            "error": self.error,
            "classification": self.classification,
        }


@dataclass
class TestReport:
    config: Dict[str, Any]
    started_at: str = field(default_factory=iso_now)
    completed_at: str = ""
    success: bool = True
    summary: str = ""
    steps: List[StepRecord] = field(default_factory=list)

    def add(self, record: StepRecord, required: bool = True) -> StepRecord:
        self.steps.append(record)
        if required and not record.success:
            self.success = False
        return record

    def finish(self, summary: str = "") -> None:
        self.completed_at = iso_now()
        self.summary = summary or ("OK" if self.success else "FAILED")

    def to_dict(self) -> Dict[str, Any]:
        return {
            "startedAt": self.started_at,
            "completedAt": self.completed_at,
            "success": self.success,
            "summary": self.summary,
            "config": self.config,
            "steps": [step.to_dict() for step in self.steps],
        }


def require_grpc():
    try:
        import grpc  # type: ignore
    except ModuleNotFoundError as exc:
        raise MissingGrpcDependency(
            "缺少 Python 依赖 grpcio。请先运行: python -m pip install grpcio\n"
            "如果 Python 3.14 暂无可用 grpcio wheel，请改用 Python 3.12/3.13 虚拟环境后再运行。"
        ) from exc

    return grpc


def encode_text(value: str) -> bytes:
    return (value or "").encode("utf-8")


def decode_text(value: bytes) -> str:
    return (value or b"").decode("utf-8")


class ControlDoorGrpcClient:
    def __init__(self, host: str, timeout_seconds: int = 60, api_key: str = ""):
        grpc = require_grpc()
        self.grpc = grpc
        self.host = host
        self.timeout_seconds = timeout_seconds
        self.api_key = api_key
        self.channel = grpc.insecure_channel(host)

    def close(self) -> None:
        close = getattr(self.channel, "close", None)
        if callable(close):
            close()

    def metadata(self) -> List[Tuple[str, str]]:
        result = [("x-request-id", uuid.uuid4().hex)]
        if self.api_key:
            result.append(("x-api-key", self.api_key))
        return result

    def call(self, method: str, payload: Any, timeout_seconds: Optional[int] = None) -> StepRecord:
        if method not in METHODS:
            raise ValueError("不支持的 gRPC 方法: " + method)

        service_name, method_name, method_type = METHODS[method]
        full_name = f"/{service_name}/{method_name}"
        request_text = payload if isinstance(payload, str) else json_dumps(payload)
        started = time.monotonic()
        record = StepRecord(name=method, request=safe_json(payload), started_at=iso_now())

        try:
            if method_type == "stream":
                stub = self.channel.unary_stream(full_name, request_serializer=encode_text, response_deserializer=decode_text)
                responses = []
                for raw in stub(request_text, timeout=timeout_seconds or self.timeout_seconds, metadata=self.metadata()):
                    responses.append(parse_json_text(raw))
                record.raw_response = responses
                record.response = responses
                record.success = len(responses) > 0
                first = responses[0] if responses else {}
                record.code = as_text(first.get("code")) if isinstance(first, dict) else ""
                record.message = as_text(first.get("message")) if isinstance(first, dict) else ""
            else:
                stub = self.channel.unary_unary(full_name, request_serializer=encode_text, response_deserializer=decode_text)
                raw = stub(request_text, timeout=timeout_seconds or self.timeout_seconds, metadata=self.metadata())
                parsed = parse_json_text(raw)
                record.raw_response = raw
                record.response = parsed
                if isinstance(parsed, dict):
                    record.success = bool(parsed.get("success"))
                    record.code = as_text(parsed.get("code"))
                    record.message = as_text(parsed.get("message"))
                else:
                    record.success = False
                    record.code = "INVALID_RESPONSE"
                    record.message = "响应不是 JSON 对象。"
        except Exception as exc:
            record.success = False
            record.error = str(exc)
            record.classification = classify_exception(exc)
            record.code = record.classification or "GRPC_ERROR"
            record.message = str(exc)
        finally:
            record.elapsed_ms = int((time.monotonic() - started) * 1000)

        if not record.classification:
            record.classification = classify_response(record.response, record.error)
        return record


def classify_exception(exc: Exception) -> str:
    text = str(exc)
    if "StatusCode.UNAVAILABLE" in text or "failed to connect" in text.lower() or "connection refused" in text.lower():
        return "GRPC_SERVICE_NOT_RUNNING"
    if "DEADLINE_EXCEEDED" in text:
        return "GRPC_TIMEOUT"
    return "GRPC_CALL_FAILED"


def classify_response(response: Any, error: str = "") -> str:
    if error:
        return "GRPC_CALL_FAILED"
    if isinstance(response, list):
        if not response:
            return "STREAM_EMPTY"
        return classify_response(response[0])
    if not isinstance(response, dict):
        return "INVALID_RESPONSE"
    if response.get("success") is True:
        return "OK"

    code = as_text(response.get("code"))
    message = as_text(response.get("message"))
    combined = (code + " " + message).lower()
    if code in {"UNAUTHENTICATED"}:
        return "GRPC_AUTH_FAILED"
    if code in {"DEVICE_OFFLINE", "DEVICE_NOT_FOUND", "DEVICE_DISABLED", "DEVICE_CONFIG_INVALID"}:
        return "DEVICE_LOGIN_OR_STATUS_FAILED"
    if code in {"PARTIAL_SUCCESS"}:
        return "SDK_OR_DEVICE_PARTIAL"
    if "queued" in combined or code == "QUEUED":
        return "SDK_RETRYABLE_OR_OFFLINE_QUEUED"
    if "sdk" in combined or "timeout" in combined or "offline" in combined or "device" in combined:
        return "SDK_OR_NETWORK_FAILURE"
    if code:
        return "DEVICE_BUSINESS_REJECTED"
    return "FAILED"


def as_text(value: Any) -> str:
    if value is None:
        return ""
    return str(value)


def parse_json_text(text: str) -> Any:
    try:
        return json.loads(text)
    except Exception:
        return text


def json_dumps(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def safe_json(value: Any) -> Any:
    if isinstance(value, str):
        parsed = parse_json_text(value)
        return parsed
    return value


def read_face_base64(path: str) -> str:
    return read_face_file(path)[0]


def read_face_file(path: str) -> Tuple[str, str]:
    if not path:
        raise SmokeFailure("强制全量测试需要提供 --face-image。")

    face_path = Path(path)
    if not face_path.exists():
        raise SmokeFailure("人脸图片不存在: " + str(face_path))

    suffix = face_path.suffix.lower()
    if suffix not in {".jpg", ".jpeg", ".png"}:
        raise SmokeFailure("人脸图片必须是 JPEG 或 PNG。")

    data = face_path.read_bytes()
    if len(data) > 200 * 1024:
        raise SmokeFailure("人脸图片超过 200KB。")

    face_format = "png" if suffix == ".png" else "jpg"
    return base64.b64encode(data).decode("ascii"), face_format


def build_config_snapshot(config: TestConfig) -> Dict[str, Any]:
    return {
        "host": config.host,
        "deviceId": config.device_id,
        "deviceIp": config.device_ip,
        "devicePort": config.device_port,
        "username": config.username,
        "passwordProvided": bool(config.password),
        "apiKeyProvided": bool(config.api_key),
        "employeeId": config.employee_id,
        "faceImage": config.face_image,
        "permissionCode": config.permission_code,
        "waitSeconds": config.wait_seconds,
        "cleanup": config.cleanup,
        "deleteDeviceRecord": config.delete_device_record,
        "full": config.full,
    }


def device_name(config: TestConfig) -> str:
    return f"真实设备-{config.device_id}"


def request_get_status(config: TestConfig, refresh: bool = False, include_disabled: bool = True) -> Dict[str, Any]:
    return {
        "includeDisabled": include_disabled,
        "refresh": refresh,
        "deviceIds": [config.device_id],
    }


def request_get_all_status(refresh: bool = False, include_disabled: bool = True) -> Dict[str, Any]:
    return {
        "includeDisabled": include_disabled,
        "refresh": refresh,
    }


def request_add_device(config: TestConfig) -> Dict[str, Any]:
    return {
        "deviceId": config.device_id,
        "deviceName": device_name(config),
        "ipAddress": config.device_ip,
        "port": config.device_port,
        "username": config.username,
        "password": config.password,
        "description": "真实设备 gRPC 联调脚本添加",
        "enabled": True,
        "connectNow": True,
    }


def request_device_id(config: TestConfig, **extra: Any) -> Dict[str, Any]:
    payload = {"deviceId": config.device_id}
    payload.update(extra)
    return payload


def request_sync_person(config: TestConfig, face_base64: str, suffix: str = "", face_format: str = "jpg") -> Dict[str, Any]:
    return {
        "items": [
            {
                "employee_id": config.employee_id,
                "name": "联调测试" + suffix,
                "gender": "unknown",
                "enabled": True,
                "valid_from": "2026-01-01T00:00:00",
                "valid_to": "2035-12-31T23:59:59",
                "face_image_base64": face_base64,
                "face_image_format": face_format or "jpg",
            }
        ]
    }


def request_sync_permission(config: TestConfig) -> Dict[str, Any]:
    return {
        "items": [
            {
                "employee_id": config.employee_id,
                "permission_code": config.permission_code,
            }
        ]
    }


def request_employee(config: TestConfig) -> Dict[str, Any]:
    return {"items": [{"employee_id": config.employee_id}]}


def request_capture(config: TestConfig) -> Dict[str, Any]:
    return {"employee_id": config.employee_id}


def request_enrollment_status(config: TestConfig, task_id: str = "") -> Dict[str, Any]:
    if task_id:
        return {"taskId": task_id}
    return {"employee_id": config.employee_id}


def response_code(record: StepRecord) -> str:
    if isinstance(record.response, dict):
        return as_text(record.response.get("code"))
    if isinstance(record.response, list) and record.response and isinstance(record.response[0], dict):
        return as_text(record.response[0].get("code"))
    return record.code


def response_success_or_partial(record: StepRecord) -> bool:
    code = response_code(record)
    return record.success or code == "PARTIAL_SUCCESS"


def extract_devices(record: StepRecord) -> List[Dict[str, Any]]:
    if isinstance(record.response, dict):
        devices = record.response.get("devices")
        if isinstance(devices, list):
            return [item for item in devices if isinstance(item, dict)]
    return []


def find_device(record: StepRecord, config: TestConfig) -> Optional[Dict[str, Any]]:
    for device in extract_devices(record):
        if int_value(device.get("deviceId")) == config.device_id:
            return device
    return None


def find_device_by_ip(record: StepRecord, config: TestConfig) -> Optional[Dict[str, Any]]:
    for device in extract_devices(record):
        if as_text(device.get("ipAddress")) == config.device_ip:
            return device
    return None


def int_value(value: Any) -> Optional[int]:
    try:
        return int(value)
    except Exception:
        return None


def assert_no_device_conflict(device: Optional[Dict[str, Any]], config: TestConfig) -> None:
    if not device:
        return
    ip = as_text(device.get("ipAddress"))
    port = int_value(device.get("port"))
    if ip and ip != config.device_ip:
        raise SmokeFailure(f"设备 ID {config.device_id} 已存在，但 IP 是 {ip}，不是 {config.device_ip}。")
    if port is not None and port != config.device_port:
        raise SmokeFailure(f"设备 ID {config.device_id} 已存在，但端口是 {port}，不是 {config.device_port}。")


def assert_no_ip_conflict(device: Optional[Dict[str, Any]], config: TestConfig) -> None:
    if not device:
        return
    device_id = int_value(device.get("deviceId"))
    if device_id is not None and device_id != config.device_id:
        raise SmokeFailure(f"设备 IP {config.device_ip} 已存在于设备 ID {device_id}，不是 {config.device_id}。")


def wait_until_connected(
    client: ControlDoorGrpcClient,
    config: TestConfig,
    report: TestReport,
    log: Callable[[str], None],
    timeout_seconds: int = 90,
) -> bool:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        record = client.call("GetDeviceStatus", request_get_status(config, refresh=True), timeout_seconds=config.timeout_seconds)
        report.add(record)
        device = find_device(record, config)
        if device and device.get("isConnected") is True:
            log("设备已连接。")
            return True
        status = device.get("status") if device else "missing"
        log(f"等待设备连接中，当前状态: {status}")
        time.sleep(5)
    return False


def wait_for_retry_scan(
    client: ControlDoorGrpcClient,
    config: TestConfig,
    report: TestReport,
    log: Callable[[str], None],
) -> None:
    deadline = time.monotonic() + max(1, config.wait_seconds)
    while time.monotonic() < deadline:
        remaining = int(deadline - time.monotonic())
        log(f"等待离线补偿扫描，剩余约 {remaining} 秒。")
        time.sleep(min(10, max(1, remaining)))
        record = client.call("GetDeviceStatus", request_get_status(config, refresh=True), timeout_seconds=config.timeout_seconds)
        report.add(record)
        device = find_device(record, config)
        if device and device.get("isConnected") is True:
            continue


def extract_task_id(record: StepRecord) -> str:
    responses = record.response if isinstance(record.response, list) else []
    for item in responses:
        if isinstance(item, dict):
            task_id = as_text(item.get("taskId"))
            if task_id:
                return task_id
    return ""


def face_exists(record: StepRecord) -> bool:
    if not isinstance(record.response, dict):
        return False
    items = record.response.get("items")
    if not isinstance(items, list):
        return False
    for item in items:
        if not isinstance(item, dict):
            continue
        devices = item.get("devices")
        if not isinstance(devices, list):
            continue
        for device in devices:
            if not isinstance(device, dict):
                continue
            if device.get("exists") is True:
                return True
            count = int_value(device.get("faceCount"))
            if count is not None and count > 0:
                return True
    return False


def assertion_step(name: str, success: bool, message: str, request: Any = None, response: Any = None) -> StepRecord:
    return StepRecord(
        name=name,
        request=request or {},
        response=response,
        raw_response=response,
        started_at=iso_now(),
        elapsed_ms=0,
        success=success,
        code="OK" if success else "ASSERTION_FAILED",
        message=message,
        classification="OK" if success else "OFFLINE_RETRY_NOT_CONFIRMED",
    )


def write_report(report: TestReport, report_path: str = "") -> Path:
    path = Path(report_path) if report_path else DEFAULT_REPORT_DIR / (datetime.now().strftime("%Y%m%d_%H%M%S") + ".json")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8")
    return path


def run_full_smoke(config: TestConfig, log: Callable[[str], None] = print) -> TestReport:
    config.ensure_employee_id()
    if not config.can_auto_cleanup_employee:
        raise SmokeFailure("强制全量测试只允许 TEST_CD_ 前缀测试员工，避免误删真实人员。")
    if not config.password:
        raise SmokeFailure("必须提供设备密码。可用 --password 或环境变量 CONTROLDOOR_DEVICE_PASSWORD。")

    face_base64, face_format = read_face_file(config.face_image)
    report = TestReport(config=build_config_snapshot(config))
    client = ControlDoorGrpcClient(config.host, config.timeout_seconds, config.api_key)
    task_id = ""

    try:
        log("1/10 查询设备状态。")
        status = report.add(client.call("GetDeviceStatus", request_get_all_status(), timeout_seconds=config.timeout_seconds))
        if status.classification == "GRPC_SERVICE_NOT_RUNNING":
            raise SmokeFailure("gRPC 服务未启动或端口不可达: " + config.host)
        device = find_device(status, config)
        ip_device = find_device_by_ip(status, config)
        assert_no_device_conflict(device, config)
        assert_no_ip_conflict(ip_device, config)

        if not device:
            log("2/10 添加真实设备。")
            added = report.add(client.call("AddDevice", request_add_device(config), timeout_seconds=config.timeout_seconds))
            if not response_success_or_partial(added):
                raise SmokeFailure("AddDevice 失败: " + added.message)
        else:
            log("2/10 设备已存在，跳过 AddDevice。")

        log("3/10 强制重连设备。")
        reconnect = report.add(client.call("ReconnectDevice", request_device_id(config, force=True), timeout_seconds=config.timeout_seconds))
        if not response_success_or_partial(reconnect):
            raise SmokeFailure("ReconnectDevice 失败: " + reconnect.message)
        if not wait_until_connected(client, config, report, log, timeout_seconds=90):
            raise SmokeFailure("设备登录失败或超时。")

        log("4/10 下发人员和人脸。")
        sync_person = report.add(client.call("SyncPersons", request_sync_person(config, face_base64, face_format=face_format), timeout_seconds=config.timeout_seconds))
        if not response_success_or_partial(sync_person):
            log("SyncPersons 未完全成功，继续记录后续接口，错误会保留在报告中。")

        log("5/10 下发权限。")
        sync_permission = report.add(client.call("SyncPermissions", request_sync_permission(config), timeout_seconds=config.timeout_seconds))
        if not response_success_or_partial(sync_permission):
            log("SyncPermissions 未完全成功，继续离线补偿验证。")

        log("6/10 查询人脸。")
        report.add(client.call("GetFaces", request_employee(config), timeout_seconds=config.timeout_seconds))

        log("7/10 采集人脸流。")
        capture = client.call("CaptureFaceStream", request_capture(config), timeout_seconds=config.timeout_seconds)
        report.add(capture, required=response_code(capture) not in {"DEVICE_ERROR", "NOT_FOUND"})
        task_id = extract_task_id(capture)

        log("8/10 查询采集状态。")
        enrollment = client.call("GetEnrollmentStatus", request_enrollment_status(config, task_id), timeout_seconds=config.timeout_seconds)
        report.add(enrollment, required=response_code(enrollment) not in {"NOT_FOUND"})

        log("9/10 验证离线补偿。")
        report.add(client.call("DeleteFaces", request_employee(config), timeout_seconds=config.timeout_seconds), required=False)
        report.add(client.call("DisconnectDevice", request_device_id(config), timeout_seconds=config.timeout_seconds))
        queued = report.add(client.call("SyncPersons", request_sync_person(config, face_base64, suffix="离线补偿", face_format=face_format), timeout_seconds=config.timeout_seconds))
        if response_code(queued) not in {"PARTIAL_SUCCESS", "OK"}:
            log("离线写操作未返回 queued/partial，继续尝试重连并记录真实结果。")
        report.add(client.call("ReconnectDevice", request_device_id(config, force=True), timeout_seconds=config.timeout_seconds))
        wait_until_connected(client, config, report, log, timeout_seconds=90)
        wait_for_retry_scan(client, config, report, log)
        retry_face = report.add(client.call("GetFaces", request_employee(config), timeout_seconds=config.timeout_seconds))
        report.add(assertion_step(
            "OfflineRetryAssertion",
            face_exists(retry_face),
            "离线 SyncPersons 补偿后已查询到测试员工人脸。" if face_exists(retry_face) else "离线 SyncPersons 补偿后未查询到测试员工人脸。",
            request={"employeeId": config.employee_id},
            response=retry_face.response,
        ))

        if config.cleanup:
            cleanup_employee(client, config, report, log)

        if config.delete_device_record:
            log("删除测试设备记录。")
            report.add(client.call("DeleteDevice", request_device_id(config, disconnectFirst=True), timeout_seconds=config.timeout_seconds))

        report.finish("强制全量联调完成。")
    except Exception as exc:
        report.success = False
        report.finish(str(exc))
    finally:
        client.close()

    return report


def cleanup_employee(client: ControlDoorGrpcClient, config: TestConfig, report: TestReport, log: Callable[[str], None]) -> None:
    if not config.can_auto_cleanup_employee:
        log("跳过清理：员工编号不是 TEST_CD_ 前缀。")
        return
    log("10/10 清理测试人脸。")
    report.add(client.call("DeleteFaces", request_employee(config), timeout_seconds=config.timeout_seconds))
    log("10/10 清理测试人员。")
    report.add(client.call("DeletePersons", request_employee(config), timeout_seconds=config.timeout_seconds))
    log("10/10 清理后查询人脸。")
    report.add(client.call("GetFaces", request_employee(config), timeout_seconds=config.timeout_seconds))


def prompt_password_if_needed(config: TestConfig) -> None:
    if config.password:
        return
    env_password = os.environ.get(PASSWORD_ENV, "")
    if env_password:
        config.password = env_password
        return
    config.password = getpass.getpass("设备密码: ")


def api_key_from_env() -> str:
    return os.environ.get(API_KEY_ENV, "")


def add_common_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--host", default=DEFAULT_HOST, help="gRPC 地址，默认 localhost:5001。")
    parser.add_argument("--device-id", type=int, default=DEFAULT_DEVICE_ID, help="设备 ID，默认 11。")
    parser.add_argument("--ip", default=DEFAULT_DEVICE_IP, help="设备 IP，默认 169.254.66.109。")
    parser.add_argument("--port", type=int, default=DEFAULT_DEVICE_PORT, help="设备 SDK 端口，默认 8000。")
    parser.add_argument("--username", default=DEFAULT_USERNAME, help="设备用户名，默认 admin。")
    parser.add_argument("--password", default="", help="设备密码；也可用 CONTROLDOOR_DEVICE_PASSWORD。")
    parser.add_argument("--api-key", default=api_key_from_env(), help="gRPC x-api-key；也可用 CONTROLDOOR_GRPC_API_KEY。")
    parser.add_argument("--employee-id", default="", help="测试员工编号；默认自动生成 TEST_CD_ 时间戳。")
    parser.add_argument("--face-image", default="", help="真实人脸 JPEG/PNG 图片，强制全量必填，最大 200KB。")
    parser.add_argument("--permission-code", type=int, default=DEFAULT_PERMISSION_CODE, help="权限码，默认 1。")
    parser.add_argument("--wait-seconds", type=int, default=DEFAULT_WAIT_SECONDS, help="离线补偿等待秒数，默认 120。")
    parser.add_argument("--timeout", type=int, default=60, help="单次 gRPC 调用超时秒数，默认 60。")
    parser.add_argument("--cleanup", action="store_true", help="结束后删除 TEST_CD_ 测试员工的人脸和人员。")
    parser.add_argument("--delete-device-record", action="store_true", help="结束后删除设备记录；默认不删除真实设备记录。")
    parser.add_argument("--report", default="", help="报告 JSON 路径，默认 artifacts/real-device-grpc/<timestamp>.json。")


def config_from_args(args: argparse.Namespace) -> TestConfig:
    config = TestConfig(
        host=args.host,
        device_id=args.device_id,
        device_ip=args.ip,
        device_port=args.port,
        username=args.username,
        password=args.password or os.environ.get(PASSWORD_ENV, ""),
        api_key=args.api_key or api_key_from_env(),
        employee_id=args.employee_id,
        face_image=args.face_image,
        permission_code=args.permission_code,
        wait_seconds=args.wait_seconds,
        timeout_seconds=args.timeout,
        cleanup=args.cleanup,
        delete_device_record=args.delete_device_record,
        report=args.report,
        full=getattr(args, "full", False),
    )
    config.ensure_employee_id()
    return config


def check_tcp_port(host: str) -> bool:
    target, _, port_text = host.partition(":")
    if not target or not port_text:
        return False
    try:
        with socket.create_connection((target, int(port_text)), timeout=2):
            return True
    except Exception:
        return False
