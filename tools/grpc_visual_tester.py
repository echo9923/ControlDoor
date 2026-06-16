#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
当前项目 gRPC 接口的 Tkinter 可视化测试器。

输入：
- gRPC 服务地址：Host、Port、Timeout、可选 x-api-key、可选/自动生成 x-request-id
- 接口请求体：界面右侧 JSON 模板编辑器中的内容

输出：
- 每次调用的耗时、gRPC 状态、业务字段摘要
- 解析后的 JSON 结果和原始响应文本

安全说明：
- “只读一键测试”只会执行 GetDeviceStatus
- 有副作用接口必须在界面中手动点击执行
- 当目标不是 localhost/127.0.0.1/::1 时，会额外弹窗确认副作用调用
"""

from __future__ import annotations

import json
import queue
import sys
import threading
import time
import uuid
from dataclasses import dataclass
from typing import Any, Callable, Dict, Iterable, Optional

import tkinter as tk
from tkinter import messagebox, ttk
from tkinter.scrolledtext import ScrolledText

try:
    import grpc
    GRPC_IMPORT_ERROR: Optional[ImportError] = None
except ImportError as exc:  # pragma: no cover - 依赖缺失由运行时提示处理
    grpc = None  # type: ignore[assignment]
    GRPC_IMPORT_ERROR = exc


APP_TITLE = "ControlEntradaSalida gRPC 可视化测试器"
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = "5001"
DEFAULT_TIMEOUT = "10"
DISPLAY_STRING_LIMIT = 4000
FACE_IMAGE_FIELDS = {"faceImageBase64", "face_image_base64"}

SERVICE_PERMISSION = "permission.PermissionSyncService"
SERVICE_DEVICE = "device.AccessControlService"

METHOD_GET_DEVICE_STATUS = "/device.AccessControlService/GetDeviceStatus"
METHOD_ADD_DEVICE = "/device.AccessControlService/AddDevice"
METHOD_DELETE_DEVICE = "/device.AccessControlService/DeleteDevice"
METHOD_DISCONNECT_DEVICE = "/device.AccessControlService/DisconnectDevice"
METHOD_RECONNECT_DEVICE = "/device.AccessControlService/ReconnectDevice"
METHOD_SYNC_PERMISSIONS = "/permission.PermissionSyncService/SyncPermissions"
METHOD_SYNC_PERSONS = "/permission.PermissionSyncService/SyncPersons"
METHOD_DELETE_FACES = "/permission.PermissionSyncService/DeleteFaces"
METHOD_DELETE_PERSONS = "/permission.PermissionSyncService/DeletePersons"
METHOD_GET_FACES = "/permission.PermissionSyncService/GetFaces"
METHOD_GET_ENROLLMENT_STATUS = "/permission.PermissionSyncService/GetEnrollmentStatus"
METHOD_CAPTURE_FACE_STREAM = "/permission.PermissionSyncService/CaptureFaceStream"

DEVICE_ID_TEMPLATE_METHODS = {
    METHOD_DELETE_DEVICE,
    METHOD_DISCONNECT_DEVICE,
    METHOD_RECONNECT_DEVICE,
}


@dataclass(frozen=True)
class MethodSpec:
    path: str
    service: str
    method: str
    kind: str
    description: str
    default_payload: str
    batch_readonly: bool = False
    remote_confirmation: bool = False
    note: str = ""

    @property
    def key(self) -> str:
        return self.path

    @property
    def kind_label(self) -> str:
        mapping = {
            "readonly": "只读",
            "effect": "有副作用",
            "stream": "流式",
        }
        return mapping.get(self.kind, self.kind)

    @property
    def tree_text(self) -> str:
        return "[{0}] {1}".format(self.kind_label, self.method)


def _json_template(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2)


def _build_get_device_status_template(device_id: Optional[int] = None) -> str:
    return _json_template(
        {
            "deviceId": device_id,
            "deviceIds": [],
            "ipAddress": "",
            "includeDisabled": True,
            "refresh": False,
        }
    )


METHOD_SPECS = [
    MethodSpec(
        path=METHOD_SYNC_PERMISSIONS,
        service=SERVICE_PERMISSION,
        method="SyncPermissions",
        kind="effect",
        description="批量同步员工权限编号。单项最小字段为 employee_id 和 permission_code。",
        default_payload=_json_template(
            [
                {
                    "employee_id": "TEST001",
                    "permission_code": 1,
                }
            ]
        ),
        remote_confirmation=True,
        note="支持数组、items 或 records。该接口会触发权限下发。",
    ),
    MethodSpec(
        path=METHOD_SYNC_PERSONS,
        service=SERVICE_PERMISSION,
        method="SyncPersons",
        kind="effect",
        description="批量同步人员信息和可选的人脸图片。最小字段为 employee_id。",
        default_payload=_json_template(
            {
                "people": [
                    {
                        "employee_id": "TEST001",
                        "name": "测试人员",
                        "gender": "unknown",
                        "enabled": True,
                        "face_image_base64": "",
                        "face_image_format": "jpg",
                    }
                ]
            }
        ),
        remote_confirmation=True,
        note="支持 people/items/records/data 数组。该接口会下发人员数据。",
    ),
    MethodSpec(
        path=METHOD_DELETE_FACES,
        service=SERVICE_PERMISSION,
        method="DeleteFaces",
        kind="effect",
        description="删除指定员工在设备上的人脸信息。",
        default_payload=_json_template({"items": [{"employee_id": "TEST001"}]}),
        remote_confirmation=True,
        note="支持字符串数组、对象数组或 items/records。该接口会删除设备侧人脸。",
    ),
    MethodSpec(
        path=METHOD_DELETE_PERSONS,
        service=SERVICE_PERMISSION,
        method="DeletePersons",
        kind="effect",
        description="删除指定员工在设备上的人员记录。",
        default_payload=_json_template({"items": [{"employee_id": "TEST001"}]}),
        remote_confirmation=True,
        note="支持字符串数组、对象数组或 items/records。该接口会删除设备侧人员。",
    ),
    MethodSpec(
        path=METHOD_GET_FACES,
        service=SERVICE_PERMISSION,
        method="GetFaces",
        kind="readonly",
        description="查询指定员工的人脸信息，返回设备侧查询结果。",
        default_payload=_json_template({"items": [{"employee_id": "TEST001"}]}),
        note="支持字符串数组、对象数组或 items/records。虽然是查询接口，但仍需人工准备员工编号。",
    ),
    MethodSpec(
        path=METHOD_GET_ENROLLMENT_STATUS,
        service=SERVICE_PERMISSION,
        method="GetEnrollmentStatus",
        kind="readonly",
        description="根据 taskId 查询录入任务状态。",
        default_payload=_json_template({"taskId": ""}),
        note="CaptureFaceStream 成功后会自动把最近 taskId 回填到这个模板。",
    ),
    MethodSpec(
        path=METHOD_CAPTURE_FACE_STREAM,
        service=SERVICE_PERMISSION,
        method="CaptureFaceStream",
        kind="stream",
        description="从录入设备抓拍人脸，服务端以流式返回任务状态和图像结果。",
        default_payload=_json_template({"employee_id": "TEST001"}),
        remote_confirmation=True,
        note="界面只展示 faceImageBase64 的长度和前缀，不渲染图片。",
    ),
    MethodSpec(
        path=METHOD_GET_DEVICE_STATUS,
        service=SERVICE_DEVICE,
        method="GetDeviceStatus",
        kind="readonly",
        description="查询全部或指定设备状态，可选 refresh 主动刷新设备状态。",
        default_payload=_build_get_device_status_template(),
        batch_readonly=True,
        note="默认模板展示全部可选字段；保留 deviceId=null、deviceIds=[]、ipAddress='' 时表示查询全部设备。设备管理接口在服务端配置了 GrpcManagementApiKey 时，需要填写 x-api-key。",
    ),
    MethodSpec(
        path=METHOD_ADD_DEVICE,
        service=SERVICE_DEVICE,
        method="AddDevice",
        kind="effect",
        description="新增设备配置，可选 connectNow 立即连接设备。",
        default_payload=_json_template(
            {
                "deviceId": 101,
                "deviceName": "测试门禁",
                "ipAddress": "192.168.1.100",
                "port": "8000",
                "username": "admin",
                "password": "123456",
                "description": "",
                "enabled": True,
                "connectNow": False,
            }
        ),
        remote_confirmation=True,
        note="执行成功后，会缓存最近 deviceId，方便后续 Delete/Disconnect/Reconnect 复用。",
    ),
    MethodSpec(
        path=METHOD_DELETE_DEVICE,
        service=SERVICE_DEVICE,
        method="DeleteDevice",
        kind="effect",
        description="删除指定设备，可选 disconnectFirst。",
        default_payload=_json_template({"deviceId": 101}),
        remote_confirmation=True,
        note="如果已有最近新增设备 ID，可一键套用到当前模板。",
    ),
    MethodSpec(
        path=METHOD_DISCONNECT_DEVICE,
        service=SERVICE_DEVICE,
        method="DisconnectDevice",
        kind="effect",
        description="断开指定设备连接。",
        default_payload=_json_template({"deviceId": 101}),
        remote_confirmation=True,
        note="如果已有最近新增设备 ID，可一键套用到当前模板。",
    ),
    MethodSpec(
        path=METHOD_RECONNECT_DEVICE,
        service=SERVICE_DEVICE,
        method="ReconnectDevice",
        kind="effect",
        description="重连指定设备，可选 force。",
        default_payload=_json_template({"deviceId": 101}),
        remote_confirmation=True,
        note="如果已有最近新增设备 ID，可一键套用到当前模板。",
    ),
]

METHOD_MAP = {spec.key: spec for spec in METHOD_SPECS}


def _serialize_text(value: str) -> bytes:
    return value.encode("utf-8")


def _deserialize_text(data: bytes) -> str:
    return data.decode("utf-8")


def _try_parse_json(text: Optional[str]) -> Any:
    if not text:
        return None

    try:
        return json.loads(text)
    except Exception:
        return None


def _normalize_host(host: str) -> str:
    normalized = (host or "").strip()
    if normalized.startswith("[") and normalized.endswith("]"):
        normalized = normalized[1:-1]
    return normalized.lower()


def _is_local_host(host: str) -> bool:
    return _normalize_host(host) in {"127.0.0.1", "localhost", "::1"}


def _build_target(host: str, port: int) -> str:
    host = (host or "").strip()
    if ":" in host and not host.startswith("["):
        return "[{0}]:{1}".format(host, port)
    return "{0}:{1}".format(host, port)


def _generate_request_id() -> str:
    return "gui-{0}".format(uuid.uuid4().hex[:12])


def _safe_pretty_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, sort_keys=False)


def _is_success_flag(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, str):
        return value.strip().lower() == "true"
    return False


def _shrink_string(value: str) -> str:
    if len(value) <= DISPLAY_STRING_LIMIT:
        return value
    return "{0}...(已截断，原始长度 {1})".format(value[:DISPLAY_STRING_LIMIT], len(value))


def _sanitize_for_display(value: Any, key: str = "") -> Any:
    if isinstance(value, dict):
        return {item_key: _sanitize_for_display(item_value, item_key) for item_key, item_value in value.items()}

    if isinstance(value, list):
        return [_sanitize_for_display(item, key) for item in value]

    if isinstance(value, str) and key in FACE_IMAGE_FIELDS:
        prefix = value[:32]
        return "<base64 长度={0} 前缀={1}>".format(len(value), prefix)

    if isinstance(value, str):
        return _shrink_string(value)

    return value


def _format_response_text(text: Optional[str]) -> str:
    parsed = _try_parse_json(text)
    if parsed is None:
        return text or "<空>"
    return _safe_pretty_json(_sanitize_for_display(parsed))


def _format_parsed_response(value: Any) -> str:
    if value is None:
        return "<未解析到 JSON>"
    return _safe_pretty_json(_sanitize_for_display(value))


def _extract_task_id(value: Any) -> Optional[str]:
    if not isinstance(value, dict):
        return None

    task_id = value.get("taskId") or value.get("task_id")
    if isinstance(task_id, str) and task_id.strip():
        return task_id.strip()
    return None


def _extract_device_id(request_object: Any, response_data: Any) -> Optional[int]:
    if isinstance(response_data, dict):
        direct = response_data.get("deviceId")
        if isinstance(direct, int):
            return direct

        nested_device = response_data.get("device")
        if isinstance(nested_device, dict):
            nested_id = nested_device.get("deviceId")
            if isinstance(nested_id, int):
                return nested_id

    if isinstance(request_object, dict):
        request_id = request_object.get("deviceId")
        if isinstance(request_id, int):
            return request_id

    return None


def _parse_rpc_error(error: Any) -> Dict[str, Any]:
    status_name = "UNKNOWN"
    details_text = ""

    try:
        status_name = error.code().name
    except Exception:
        status_name = "UNKNOWN"

    try:
        details_text = error.details() or ""
    except Exception:
        details_text = str(error)

    parsed = _try_parse_json(details_text)
    if isinstance(parsed, dict):
        parsed = dict(parsed)
        parsed.setdefault("grpcStatus", status_name)
        return {
            "grpc_status": status_name,
            "response_text": details_text,
            "response_data": parsed,
        }

    fallback_payload = {
        "success": False,
        "code": "RPC_ERROR",
        "message": details_text or str(error),
        "grpcStatus": status_name,
    }
    return {
        "grpc_status": status_name,
        "response_text": details_text or str(error),
        "response_data": fallback_payload,
    }


def _build_metadata(spec: MethodSpec, api_key: str, request_id: str) -> Iterable[tuple[str, str]]:
    metadata = []
    if request_id:
        metadata.append(("x-request-id", request_id))
    if spec.service == SERVICE_DEVICE and api_key:
        metadata.append(("x-api-key", api_key))
    return metadata


def _probe_target(target: str, timeout: float) -> Dict[str, Any]:
    if grpc is None:  # pragma: no cover - 运行前已拦截
        raise RuntimeError("grpcio 未安装。")

    started = time.perf_counter()
    with grpc.insecure_channel(target) as channel:
        grpc.channel_ready_future(channel).result(timeout=timeout)
    elapsed = time.perf_counter() - started
    return {"elapsed": elapsed}


def _invoke_unary(
    spec: MethodSpec,
    target: str,
    payload_text: str,
    timeout: float,
    metadata: Iterable[tuple[str, str]],
) -> Dict[str, Any]:
    if grpc is None:  # pragma: no cover - 运行前已拦截
        raise RuntimeError("grpcio 未安装。")

    started = time.perf_counter()
    with grpc.insecure_channel(target) as channel:
        callable_object = channel.unary_unary(
            spec.path,
            request_serializer=_serialize_text,
            response_deserializer=_deserialize_text,
        )
        try:
            response_text = callable_object(payload_text, timeout=timeout, metadata=list(metadata))
            parsed = _try_parse_json(response_text)
            if isinstance(parsed, dict):
                parsed = dict(parsed)
                parsed.setdefault("grpcStatus", "OK")
            elapsed = time.perf_counter() - started
            return {
                "elapsed": elapsed,
                "grpc_status": "OK",
                "response_text": response_text,
                "response_data": parsed,
            }
        except grpc.RpcError as error:
            elapsed = time.perf_counter() - started
            parsed_error = _parse_rpc_error(error)
            parsed_error["elapsed"] = elapsed
            return parsed_error


class GrpcVisualTesterApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("1400x860")
        self.minsize(1180, 720)

        self.event_queue: "queue.Queue[Dict[str, Any]]" = queue.Queue()
        self.payload_cache = {spec.key: spec.default_payload for spec in METHOD_SPECS}
        self.selected_method_key = METHOD_GET_DEVICE_STATUS
        self.last_added_device_id: Optional[int] = None
        self.last_enrollment_task_id: Optional[str] = None
        self.busy = False

        self.host_var = tk.StringVar(value=DEFAULT_HOST)
        self.port_var = tk.StringVar(value=DEFAULT_PORT)
        self.timeout_var = tk.StringVar(value=DEFAULT_TIMEOUT)
        self.api_key_var = tk.StringVar(value="")
        self.request_id_var = tk.StringVar(value="")
        self.auto_request_id_var = tk.BooleanVar(value=True)

        self.method_title_var = tk.StringVar(value="")
        self.method_kind_var = tk.StringVar(value="")
        self.method_path_var = tk.StringVar(value="")
        self.method_note_var = tk.StringVar(value="")
        self.candidate_var = tk.StringVar(value="最近缓存：无")
        self.result_summary_var = tk.StringVar(value="结果摘要：等待执行")
        self.status_var = tk.StringVar(value="就绪")

        self._build_ui()
        self.after(100, self._poll_events)
        self._select_initial_method()

    def _build_ui(self) -> None:
        self.columnconfigure(0, weight=1)
        self.rowconfigure(1, weight=1)

        top_frame = ttk.LabelFrame(self, text="连接设置")
        top_frame.grid(row=0, column=0, sticky="nsew", padx=10, pady=(10, 6))
        for column in range(10):
            top_frame.columnconfigure(column, weight=0)
        top_frame.columnconfigure(8, weight=1)

        ttk.Label(top_frame, text="Host").grid(row=0, column=0, padx=(8, 4), pady=8, sticky="w")
        self.host_entry = ttk.Entry(top_frame, textvariable=self.host_var, width=18)
        self.host_entry.grid(row=0, column=1, padx=4, pady=8, sticky="w")

        ttk.Label(top_frame, text="Port").grid(row=0, column=2, padx=(8, 4), pady=8, sticky="w")
        self.port_entry = ttk.Entry(top_frame, textvariable=self.port_var, width=8)
        self.port_entry.grid(row=0, column=3, padx=4, pady=8, sticky="w")

        ttk.Label(top_frame, text="Timeout(s)").grid(row=0, column=4, padx=(8, 4), pady=8, sticky="w")
        self.timeout_entry = ttk.Entry(top_frame, textvariable=self.timeout_var, width=8)
        self.timeout_entry.grid(row=0, column=5, padx=4, pady=8, sticky="w")

        ttk.Label(top_frame, text="x-api-key").grid(row=0, column=6, padx=(8, 4), pady=8, sticky="w")
        self.api_key_entry = ttk.Entry(top_frame, textvariable=self.api_key_var, width=24)
        self.api_key_entry.grid(row=0, column=7, padx=4, pady=8, sticky="we")

        self.probe_button = ttk.Button(top_frame, text="连接探测", command=self._run_probe)
        self.probe_button.grid(row=0, column=8, padx=4, pady=8, sticky="e")

        self.batch_button = ttk.Button(top_frame, text="只读一键测试", command=self._run_readonly_batch)
        self.batch_button.grid(row=0, column=9, padx=(4, 8), pady=8, sticky="e")

        ttk.Checkbutton(
            top_frame,
            text="自动生成 x-request-id",
            variable=self.auto_request_id_var,
        ).grid(row=1, column=0, columnspan=3, padx=(8, 4), pady=(0, 8), sticky="w")

        ttk.Label(top_frame, text="手动 request-id").grid(row=1, column=3, padx=(8, 4), pady=(0, 8), sticky="w")
        self.request_id_entry = ttk.Entry(top_frame, textvariable=self.request_id_var, width=28)
        self.request_id_entry.grid(row=1, column=4, columnspan=3, padx=4, pady=(0, 8), sticky="we")

        ttk.Label(
            top_frame,
            text="提示：批量测试只执行 GetDeviceStatus；其它接口全部保留手动点测。",
        ).grid(row=1, column=7, columnspan=3, padx=(8, 8), pady=(0, 8), sticky="e")

        body = ttk.Panedwindow(self, orient=tk.HORIZONTAL)
        body.grid(row=1, column=0, sticky="nsew", padx=10, pady=6)

        left_frame = ttk.Frame(body)
        right_frame = ttk.Frame(body)
        body.add(left_frame, weight=1)
        body.add(right_frame, weight=3)

        left_frame.columnconfigure(0, weight=1)
        left_frame.rowconfigure(1, weight=1)

        ttk.Label(left_frame, text="接口列表").grid(row=0, column=0, sticky="nw", pady=(0, 6))
        self.method_tree = ttk.Treeview(left_frame, show="tree", selectmode="browse", height=24)
        self.method_tree.grid(row=1, column=0, sticky="nsew")
        tree_scroll = ttk.Scrollbar(left_frame, orient=tk.VERTICAL, command=self.method_tree.yview)
        tree_scroll.grid(row=1, column=1, sticky="ns")
        self.method_tree.configure(yscrollcommand=tree_scroll.set)
        self.method_tree.bind("<<TreeviewSelect>>", self._on_tree_select)

        permission_root = self.method_tree.insert("", tk.END, iid=SERVICE_PERMISSION, text=SERVICE_PERMISSION, open=True)
        device_root = self.method_tree.insert("", tk.END, iid=SERVICE_DEVICE, text=SERVICE_DEVICE, open=True)
        for spec in METHOD_SPECS:
            parent = permission_root if spec.service == SERVICE_PERMISSION else device_root
            self.method_tree.insert(parent, tk.END, iid=spec.key, text=spec.tree_text)

        right_frame.columnconfigure(0, weight=1)
        right_frame.rowconfigure(2, weight=1)

        info_frame = ttk.LabelFrame(right_frame, text="接口详情")
        info_frame.grid(row=0, column=0, sticky="nsew")
        info_frame.columnconfigure(1, weight=1)

        ttk.Label(info_frame, text="方法").grid(row=0, column=0, sticky="nw", padx=8, pady=(8, 4))
        ttk.Label(info_frame, textvariable=self.method_title_var).grid(row=0, column=1, sticky="nw", padx=8, pady=(8, 4))

        ttk.Label(info_frame, text="类型").grid(row=1, column=0, sticky="nw", padx=8, pady=4)
        ttk.Label(info_frame, textvariable=self.method_kind_var).grid(row=1, column=1, sticky="nw", padx=8, pady=4)

        ttk.Label(info_frame, text="路径").grid(row=2, column=0, sticky="nw", padx=8, pady=4)
        ttk.Label(info_frame, textvariable=self.method_path_var).grid(row=2, column=1, sticky="nw", padx=8, pady=4)

        ttk.Label(info_frame, text="说明").grid(row=3, column=0, sticky="nw", padx=8, pady=4)
        self.description_label = ttk.Label(info_frame, text="", wraplength=860, justify=tk.LEFT)
        self.description_label.grid(row=3, column=1, sticky="nw", padx=8, pady=4)

        ttk.Label(info_frame, text="备注").grid(row=4, column=0, sticky="nw", padx=8, pady=(4, 8))
        ttk.Label(info_frame, textvariable=self.method_note_var, wraplength=860, justify=tk.LEFT).grid(
            row=4, column=1, sticky="nw", padx=8, pady=(4, 8)
        )

        payload_frame = ttk.LabelFrame(right_frame, text="请求模板")
        payload_frame.grid(row=1, column=0, sticky="nsew", pady=(8, 8))
        payload_frame.columnconfigure(0, weight=1)
        payload_frame.rowconfigure(1, weight=1)

        action_row = ttk.Frame(payload_frame)
        action_row.grid(row=0, column=0, sticky="ew", padx=8, pady=(8, 4))
        action_row.columnconfigure(5, weight=1)

        ttk.Button(action_row, text="恢复默认模板", command=self._reset_template).grid(row=0, column=0, padx=(0, 6))
        ttk.Button(action_row, text="格式化 JSON", command=self._format_payload_json).grid(row=0, column=1, padx=6)
        self.apply_candidate_button = ttk.Button(action_row, text="套用最近候选", command=self._apply_candidate_to_selected)
        self.apply_candidate_button.grid(row=0, column=2, padx=6)
        self.execute_button = ttk.Button(action_row, text="执行当前接口", command=self._run_selected_method)
        self.execute_button.grid(row=0, column=3, padx=6)
        ttk.Label(action_row, textvariable=self.candidate_var).grid(row=0, column=5, sticky="e")

        self.payload_text = ScrolledText(payload_frame, wrap=tk.NONE, height=16, undo=True)
        self.payload_text.grid(row=1, column=0, sticky="nsew", padx=8, pady=(0, 8))

        result_frame = ttk.LabelFrame(right_frame, text="执行结果")
        result_frame.grid(row=2, column=0, sticky="nsew")
        result_frame.columnconfigure(0, weight=1)
        result_frame.rowconfigure(1, weight=1)

        ttk.Label(result_frame, textvariable=self.result_summary_var).grid(row=0, column=0, sticky="w", padx=8, pady=(8, 4))
        self.result_text = ScrolledText(result_frame, wrap=tk.NONE, height=18, undo=False)
        self.result_text.grid(row=1, column=0, sticky="nsew", padx=8, pady=(0, 8))

        status_bar = ttk.Label(self, textvariable=self.status_var, relief=tk.SUNKEN, anchor="w")
        status_bar.grid(row=2, column=0, sticky="ew", padx=10, pady=(0, 10))

    def _select_initial_method(self) -> None:
        self.method_tree.selection_set(self.selected_method_key)
        self.method_tree.focus(self.selected_method_key)
        self._load_selected_method(self.selected_method_key)

    def _save_current_payload(self) -> None:
        if not self.selected_method_key:
            return
        self.payload_cache[self.selected_method_key] = self.payload_text.get("1.0", tk.END).strip()

    def _set_payload_text(self, text: str) -> None:
        self.payload_text.delete("1.0", tk.END)
        self.payload_text.insert("1.0", text)

    def _set_result_text(self, text: str) -> None:
        self.result_text.delete("1.0", tk.END)
        self.result_text.insert("1.0", text)
        self.result_text.see(tk.END)

    def _append_result_text(self, text: str) -> None:
        self.result_text.insert(tk.END, text)
        if not text.endswith("\n"):
            self.result_text.insert(tk.END, "\n")
        self.result_text.see(tk.END)

    def _set_status(self, message: str) -> None:
        self.status_var.set(message)

    def _set_busy(self, busy: bool, message: str) -> None:
        self.busy = busy
        probe_state = tk.DISABLED if busy else tk.NORMAL
        self.probe_button.configure(state=probe_state)
        self.batch_button.configure(state=probe_state)
        self.execute_button.configure(state=probe_state)
        self._refresh_candidate_hint()
        self._set_status(message)

    def _on_tree_select(self, _event: Any) -> None:
        selected_items = self.method_tree.selection()
        if not selected_items:
            return

        selected = selected_items[0]
        if selected not in METHOD_MAP:
            return

        self._save_current_payload()
        self._load_selected_method(selected)

    def _load_selected_method(self, method_key: str) -> None:
        spec = METHOD_MAP[method_key]
        self.selected_method_key = method_key
        self.method_title_var.set("{0}.{1}".format(spec.service, spec.method))
        self.method_kind_var.set(spec.kind_label)
        self.method_path_var.set(spec.path)
        self.description_label.configure(text=spec.description)
        self.method_note_var.set(spec.note or "无")
        self._set_payload_text(self.payload_cache.get(method_key, spec.default_payload))
        self._refresh_candidate_hint()

    def _refresh_candidate_hint(self) -> None:
        spec = METHOD_MAP[self.selected_method_key]
        enabled = not self.busy
        if spec.key == METHOD_GET_ENROLLMENT_STATUS and self.last_enrollment_task_id:
            self.candidate_var.set("最近缓存：taskId = {0}".format(self.last_enrollment_task_id))
            self.apply_candidate_button.configure(state=tk.NORMAL if enabled else tk.DISABLED)
            return

        if spec.key == METHOD_GET_DEVICE_STATUS and self.last_added_device_id is not None:
            self.candidate_var.set("最近缓存：deviceId = {0}".format(self.last_added_device_id))
            self.apply_candidate_button.configure(state=tk.NORMAL if enabled else tk.DISABLED)
            return

        if spec.key in DEVICE_ID_TEMPLATE_METHODS and self.last_added_device_id is not None:
            self.candidate_var.set("最近缓存：deviceId = {0}".format(self.last_added_device_id))
            self.apply_candidate_button.configure(state=tk.NORMAL if enabled else tk.DISABLED)
            return

        self.candidate_var.set("最近缓存：无")
        self.apply_candidate_button.configure(state=tk.DISABLED)

    def _reset_template(self) -> None:
        spec = METHOD_MAP[self.selected_method_key]
        self.payload_cache[spec.key] = spec.default_payload
        self._set_payload_text(spec.default_payload)
        self._set_status("已恢复默认模板。")

    def _format_payload_json(self) -> None:
        text = self.payload_text.get("1.0", tk.END).strip()
        if not text:
            messagebox.showwarning("提示", "请求模板不能为空。")
            return

        try:
            formatted = _safe_pretty_json(json.loads(text))
        except Exception as error:
            messagebox.showerror("JSON 格式错误", "当前模板不是有效 JSON：\n{0}".format(error))
            return

        self._set_payload_text(formatted)
        self._set_status("JSON 已格式化。")

    def _apply_candidate_to_selected(self) -> None:
        spec = METHOD_MAP[self.selected_method_key]
        if spec.key == METHOD_GET_ENROLLMENT_STATUS and self.last_enrollment_task_id:
            text = _json_template({"taskId": self.last_enrollment_task_id})
            self.payload_cache[spec.key] = text
            self._set_payload_text(text)
            self._set_status("已套用最近 taskId。")
            return

        if spec.key == METHOD_GET_DEVICE_STATUS and self.last_added_device_id is not None:
            text = _build_get_device_status_template(self.last_added_device_id)
            self.payload_cache[spec.key] = text
            self._set_payload_text(text)
            self._set_status("已套用最近 deviceId。")
            return

        if spec.key in DEVICE_ID_TEMPLATE_METHODS and self.last_added_device_id is not None:
            text = _json_template({"deviceId": self.last_added_device_id})
            self.payload_cache[spec.key] = text
            self._set_payload_text(text)
            self._set_status("已套用最近 deviceId。")
            return

        messagebox.showinfo("提示", "当前接口没有可用的最近候选。")

    def _validate_common_inputs(self) -> Optional[Dict[str, Any]]:
        host = self.host_var.get().strip()
        if not host:
            messagebox.showerror("参数错误", "Host 不能为空。")
            return None

        try:
            port = int(self.port_var.get().strip())
        except Exception:
            messagebox.showerror("参数错误", "Port 必须是整数。")
            return None

        if port <= 0 or port > 65535:
            messagebox.showerror("参数错误", "Port 必须在 1-65535 范围内。")
            return None

        try:
            timeout = float(self.timeout_var.get().strip())
        except Exception:
            messagebox.showerror("参数错误", "Timeout 必须是数字。")
            return None

        if timeout <= 0:
            messagebox.showerror("参数错误", "Timeout 必须大于 0。")
            return None

        return {
            "host": host,
            "port": port,
            "timeout": timeout,
            "target": _build_target(host, port),
            "api_key": self.api_key_var.get().strip(),
        }

    def _resolve_request_id(self) -> str:
        if self.auto_request_id_var.get():
            request_id = _generate_request_id()
            self.request_id_var.set(request_id)
            return request_id
        return self.request_id_var.get().strip()

    def _confirm_if_needed(self, spec: MethodSpec, host: str) -> bool:
        if not spec.remote_confirmation:
            return True

        if _is_local_host(host):
            return True

        return messagebox.askokcancel(
            "确认副作用调用",
            "当前目标不是本机：{0}\n接口 {1} 可能会修改远端设备或人员数据。\n确认继续执行吗？".format(host, spec.method),
        )

    def _prepare_method_context(self, spec: MethodSpec) -> Optional[Dict[str, Any]]:
        common = self._validate_common_inputs()
        if common is None:
            return None

        if not self._confirm_if_needed(spec, common["host"]):
            return None

        self._save_current_payload()
        payload_text = self.payload_cache.get(spec.key, "").strip()
        if not payload_text:
            messagebox.showerror("参数错误", "请求模板不能为空。")
            return None

        try:
            payload_object = json.loads(payload_text)
        except Exception as error:
            messagebox.showerror("JSON 格式错误", "当前模板不是有效 JSON：\n{0}".format(error))
            return None

        compact_payload = json.dumps(payload_object, ensure_ascii=False, separators=(",", ":"))
        request_id = self._resolve_request_id()
        metadata = list(_build_metadata(spec, common["api_key"], request_id))

        common.update(
            {
                "spec": spec,
                "payload_object": payload_object,
                "payload_text": compact_payload,
                "request_id": request_id,
                "metadata": metadata,
            }
        )
        return common

    def _run_in_background(self, action_name: str, worker: Callable[[], None]) -> None:
        if self.busy:
            messagebox.showinfo("提示", "当前已有请求在执行，请稍候。")
            return

        self._set_busy(True, "正在执行：{0}".format(action_name))

        def runner() -> None:
            try:
                worker()
            except Exception as error:
                self.event_queue.put(
                    {
                        "type": "task_error",
                        "action": action_name,
                        "message": str(error),
                    }
                )
            finally:
                self.event_queue.put({"type": "task_done", "action": action_name})

        thread = threading.Thread(target=runner, name="grpc-visual-tester", daemon=True)
        thread.start()

    def _run_probe(self) -> None:
        common = self._validate_common_inputs()
        if common is None:
            return

        self.result_summary_var.set("结果摘要：正在探测连接")
        self._set_result_text("准备探测 {0} ...\n".format(common["target"]))

        def worker() -> None:
            result = _probe_target(common["target"], common["timeout"])
            self.event_queue.put(
                {
                    "type": "probe_result",
                    "target": common["target"],
                    "elapsed": result["elapsed"],
                }
            )

        self._run_in_background("连接探测", worker)

    def _run_readonly_batch(self) -> None:
        spec = METHOD_MAP[METHOD_GET_DEVICE_STATUS]
        if self.selected_method_key == spec.key:
            self._save_current_payload()

        common = self._validate_common_inputs()
        if common is None:
            return

        payload_text = self.payload_cache.get(spec.key, spec.default_payload).strip() or spec.default_payload
        try:
            payload_object = json.loads(payload_text)
        except Exception as error:
            messagebox.showerror("JSON 格式错误", "GetDeviceStatus 模板不是有效 JSON：\n{0}".format(error))
            return

        compact_payload = json.dumps(payload_object, ensure_ascii=False, separators=(",", ":"))
        request_id = self._resolve_request_id()
        metadata = list(_build_metadata(spec, common["api_key"], request_id))

        self.result_summary_var.set("结果摘要：正在执行只读一键测试")
        self._set_result_text("只读一键测试将执行：{0}\n\n".format(spec.path))

        def worker() -> None:
            result = _invoke_unary(spec, common["target"], compact_payload, common["timeout"], metadata)
            self.event_queue.put(
                {
                    "type": "unary_result",
                    "spec_key": spec.key,
                    "request_id": request_id,
                    "target": common["target"],
                    "payload_text": compact_payload,
                    "payload_object": payload_object,
                    "grpc_status": result["grpc_status"],
                    "response_text": result["response_text"],
                    "response_data": result["response_data"],
                    "elapsed": result["elapsed"],
                    "origin": "readonly_batch",
                }
            )

        self._run_in_background("只读一键测试", worker)

    def _run_selected_method(self) -> None:
        spec = METHOD_MAP[self.selected_method_key]
        context = self._prepare_method_context(spec)
        if context is None:
            return

        self.result_summary_var.set("结果摘要：正在执行 {0}".format(spec.method))
        self._set_result_text(
            "方法：{0}\n目标：{1}\n请求 ID：{2}\n\n请求体：\n{3}\n\n".format(
                spec.path,
                context["target"],
                context["request_id"] or "<未设置>",
                _safe_pretty_json(context["payload_object"]),
            )
        )

        if spec.kind == "stream":
            def stream_worker() -> None:
                self._invoke_stream_worker(context)

            self._run_in_background("执行 {0}".format(spec.method), stream_worker)
            return

        def unary_worker() -> None:
            result = _invoke_unary(
                spec,
                context["target"],
                context["payload_text"],
                context["timeout"],
                context["metadata"],
            )
            self.event_queue.put(
                {
                    "type": "unary_result",
                    "spec_key": spec.key,
                    "request_id": context["request_id"],
                    "target": context["target"],
                    "payload_text": context["payload_text"],
                    "payload_object": context["payload_object"],
                    "grpc_status": result["grpc_status"],
                    "response_text": result["response_text"],
                    "response_data": result["response_data"],
                    "elapsed": result["elapsed"],
                    "origin": "single",
                }
            )

        self._run_in_background("执行 {0}".format(spec.method), unary_worker)

    def _invoke_stream_worker(self, context: Dict[str, Any]) -> None:
        spec = context["spec"]
        if grpc is None:  # pragma: no cover - 运行前已拦截
            raise RuntimeError("grpcio 未安装。")

        started = time.perf_counter()
        item_count = 0
        with grpc.insecure_channel(context["target"]) as channel:
            callable_object = channel.unary_stream(
                spec.path,
                request_serializer=_serialize_text,
                response_deserializer=_deserialize_text,
            )
            try:
                responses = callable_object(
                    context["payload_text"],
                    timeout=context["timeout"],
                    metadata=list(context["metadata"]),
                )
                for index, response_text in enumerate(responses, start=1):
                    item_count = index
                    parsed = _try_parse_json(response_text)
                    if isinstance(parsed, dict):
                        parsed = dict(parsed)
                        parsed.setdefault("grpcStatus", "OK")
                    self.event_queue.put(
                        {
                            "type": "stream_item",
                            "spec_key": spec.key,
                            "request_id": context["request_id"],
                            "target": context["target"],
                            "payload_text": context["payload_text"],
                            "payload_object": context["payload_object"],
                            "grpc_status": "OK",
                            "response_text": response_text,
                            "response_data": parsed,
                            "elapsed": time.perf_counter() - started,
                            "index": index,
                        }
                    )
            except grpc.RpcError as error:
                parsed_error = _parse_rpc_error(error)
                self.event_queue.put(
                    {
                        "type": "stream_item",
                        "spec_key": spec.key,
                        "request_id": context["request_id"],
                        "target": context["target"],
                        "payload_text": context["payload_text"],
                        "payload_object": context["payload_object"],
                        "grpc_status": parsed_error["grpc_status"],
                        "response_text": parsed_error["response_text"],
                        "response_data": parsed_error["response_data"],
                        "elapsed": time.perf_counter() - started,
                        "index": item_count + 1,
                    }
                )
            finally:
                self.event_queue.put(
                    {
                        "type": "stream_complete",
                        "spec_key": spec.key,
                        "elapsed": time.perf_counter() - started,
                        "count": item_count,
                    }
                )

    def _poll_events(self) -> None:
        while True:
            try:
                event = self.event_queue.get_nowait()
            except queue.Empty:
                break

            event_type = event.get("type")
            if event_type == "probe_result":
                self._handle_probe_result(event)
            elif event_type == "unary_result":
                self._handle_unary_result(event)
            elif event_type == "stream_item":
                self._handle_stream_item(event)
            elif event_type == "stream_complete":
                self._handle_stream_complete(event)
            elif event_type == "task_error":
                self._handle_task_error(event)
            elif event_type == "task_done":
                self._handle_task_done(event)

        self.after(100, self._poll_events)

    def _handle_probe_result(self, event: Dict[str, Any]) -> None:
        text = (
            "连接探测成功\n"
            "目标：{0}\n"
            "耗时：{1:.3f}s\n"
        ).format(event["target"], event["elapsed"])
        self.result_summary_var.set("结果摘要：连接探测成功")
        self._set_result_text(text)
        self._set_status("连接探测成功：{0}".format(event["target"]))

    def _handle_unary_result(self, event: Dict[str, Any]) -> None:
        spec = METHOD_MAP[event["spec_key"]]
        response_data = event["response_data"]
        response_text = event["response_text"]
        grpc_status = event["grpc_status"]
        success = _is_success_flag(response_data.get("success")) if isinstance(response_data, dict) else False
        code = response_data.get("code", "<未知>") if isinstance(response_data, dict) else "<未知>"
        message = response_data.get("message", "<空>") if isinstance(response_data, dict) else "<空>"
        request_id = event.get("request_id") or (response_data.get("requestId") if isinstance(response_data, dict) else "") or "<未设置>"

        summary = "结果摘要：{0} | success={1} | code={2} | grpc={3}".format(
            spec.method,
            success,
            code,
            grpc_status,
        )
        self.result_summary_var.set(summary)

        parts = [
            "方法：{0}".format(spec.path),
            "目标：{0}".format(event["target"]),
            "请求 ID：{0}".format(request_id),
            "耗时：{0:.3f}s".format(event["elapsed"]),
            "gRPC 状态：{0}".format(grpc_status),
            "业务 success：{0}".format(success),
            "业务 code：{0}".format(code),
            "业务 message：{0}".format(message),
            "",
            "请求体：",
            _safe_pretty_json(event["payload_object"]),
            "",
            "解析结果：",
            _format_parsed_response(response_data),
            "",
            "原始响应：",
            _format_response_text(response_text),
        ]

        self._set_result_text("\n".join(parts))
        self._set_status("{0} 执行完成。".format(spec.method))
        self._update_follow_up_templates(spec, event["payload_object"], response_data)

    def _handle_stream_item(self, event: Dict[str, Any]) -> None:
        spec = METHOD_MAP[event["spec_key"]]
        response_data = event["response_data"]
        response_text = event["response_text"]
        grpc_status = event["grpc_status"]
        request_id = event.get("request_id") or (response_data.get("requestId") if isinstance(response_data, dict) else "") or "<未设置>"
        success = _is_success_flag(response_data.get("success")) if isinstance(response_data, dict) else False
        code = response_data.get("code", "<未知>") if isinstance(response_data, dict) else "<未知>"
        message = response_data.get("message", "<空>") if isinstance(response_data, dict) else "<空>"

        if event["index"] == 1:
            self._set_result_text(
                "方法：{0}\n目标：{1}\n请求 ID：{2}\n\n请求体：\n{3}\n\n".format(
                    spec.path,
                    event["target"],
                    request_id,
                    _safe_pretty_json(event["payload_object"]),
                )
            )

        self.result_summary_var.set(
            "结果摘要：{0} 已接收 {1} 条流式消息".format(spec.method, event["index"])
        )
        self._append_result_text(
            "----- 第 {0} 条流式消息 | {1:.3f}s -----\n"
            "gRPC 状态：{2}\n"
            "业务 success：{3}\n"
            "业务 code：{4}\n"
            "业务 message：{5}\n"
            "解析结果：\n{6}\n"
            "原始响应：\n{7}\n".format(
                event["index"],
                event["elapsed"],
                grpc_status,
                success,
                code,
                message,
                _format_parsed_response(response_data),
                _format_response_text(response_text),
            )
        )
        self._set_status("{0} 正在接收流式消息，第 {1} 条。".format(spec.method, event["index"]))
        self._update_follow_up_templates(spec, event["payload_object"], response_data)

    def _handle_stream_complete(self, event: Dict[str, Any]) -> None:
        spec = METHOD_MAP[event["spec_key"]]
        self._append_result_text(
            "\n流式调用结束：共接收 {0} 条消息，总耗时 {1:.3f}s\n".format(event["count"], event["elapsed"])
        )
        self.result_summary_var.set(
            "结果摘要：{0} 流式结束，共 {1} 条".format(spec.method, event["count"])
        )
        self._set_status("{0} 流式调用完成。".format(spec.method))

    def _handle_task_error(self, event: Dict[str, Any]) -> None:
        self.result_summary_var.set("结果摘要：执行失败")
        self._append_result_text("\n执行异常：{0}\n".format(event["message"]))
        self._set_status("{0} 失败：{1}".format(event["action"], event["message"]))

    def _handle_task_done(self, event: Dict[str, Any]) -> None:
        self._set_busy(False, "{0} 已完成。".format(event["action"]))

    def _update_follow_up_templates(self, spec: MethodSpec, request_object: Any, response_data: Any) -> None:
        if spec.key == METHOD_CAPTURE_FACE_STREAM:
            task_id = _extract_task_id(response_data)
            if task_id:
                self.last_enrollment_task_id = task_id
                template = _json_template({"taskId": task_id})
                self.payload_cache[METHOD_GET_ENROLLMENT_STATUS] = template
                if self.selected_method_key == METHOD_GET_ENROLLMENT_STATUS:
                    self._set_payload_text(template)

        if spec.key == METHOD_ADD_DEVICE and isinstance(response_data, dict) and _is_success_flag(response_data.get("success")):
            device_id = _extract_device_id(request_object, response_data)
            if device_id is not None:
                self.last_added_device_id = device_id


def _show_dependency_error(message: str) -> None:
    try:
        root = tk.Tk()
        root.withdraw()
        messagebox.showerror("缺少依赖", message)
        root.destroy()
    except tk.TclError:
        pass
    print(message, file=sys.stderr)


def main() -> int:
    if GRPC_IMPORT_ERROR is not None:
        message = (
            "当前环境缺少 grpcio，无法启动 gRPC 可视化测试器。\n\n"
            "请先执行：\n"
            "  py -m pip install grpcio\n\n"
            "原始错误：{0}".format(GRPC_IMPORT_ERROR)
        )
        _show_dependency_error(message)
        return 1

    app = GrpcVisualTesterApp()
    app.mainloop()
    return 0


if __name__ == "__main__":
    sys.exit(main())
