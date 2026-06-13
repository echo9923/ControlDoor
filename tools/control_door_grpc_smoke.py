import argparse
import json
import sys

from control_door_grpc_common import (
    METHODS,
    MissingGrpcDependency,
    SmokeFailure,
    TestReport,
    add_common_arguments,
    build_config_snapshot,
    check_tcp_port,
    cleanup_employee,
    config_from_args,
    prompt_password_if_needed,
    request_add_device,
    request_capture,
    request_device_id,
    request_employee,
    request_enrollment_status,
    request_get_status,
    request_sync_permission,
    request_sync_person,
    run_full_smoke,
    write_report,
    ControlDoorGrpcClient,
    read_face_file,
)


def main() -> int:
    parser = argparse.ArgumentParser(description="ControlDoor 真实设备 gRPC 联调脚本")
    add_common_arguments(parser)
    parser.add_argument("--full", action="store_true", help="执行强制全量真实设备联调。")
    parser.add_argument(
        "--method",
        choices=sorted(METHODS.keys()),
        help="只调用单个 gRPC 方法；未指定且未启用 --full 时默认 GetDeviceStatus。",
    )
    parser.add_argument("--payload", default="", help="单方法调用的原始 JSON 请求。")
    parser.add_argument("--pretty", action="store_true", help="控制台格式化输出 JSON。")
    args = parser.parse_args()

    config = config_from_args(args)

    report = None
    try:
        if args.full:
            prompt_password_if_needed(config)
            if not check_tcp_port(config.host):
                print("警告: gRPC 端口当前不可达，仍会继续调用并把错误写入报告。", file=sys.stderr)
            report = run_full_smoke(config)
            path = write_report(report, config.report)
            print("报告: " + str(path))
            print("结果: " + ("OK" if report.success else "FAILED"))
            return 0 if report.success else 1

        report = TestReport(config=build_config_snapshot(config))
        client = ControlDoorGrpcClient(config.host, config.timeout_seconds, config.api_key)
        try:
            method = args.method or "GetDeviceStatus"
            payload = resolve_payload(method, args.payload, config)
            record = client.call(method, payload, timeout_seconds=config.timeout_seconds)
            report.add(record)
            report.finish(record.message or record.code)
            path = write_report(report, config.report)
            output = record.to_dict()
            print(json.dumps(output, ensure_ascii=False, indent=2 if args.pretty else None))
            print("报告: " + str(path), file=sys.stderr)
            return 0 if record.success else 1
        finally:
            client.close()
    except MissingGrpcDependency as exc:
        print(str(exc), file=sys.stderr)
        return 3
    except (SmokeFailure, Exception) as exc:
        print("联调失败: " + str(exc), file=sys.stderr)
        try:
            if report is not None:
                if not report.completed_at:
                    report.success = False
                    report.finish(str(exc))
                path = write_report(report, config.report)
                print("失败报告: " + str(path), file=sys.stderr)
        except Exception:
            pass
        return 2


def resolve_payload(method: str, raw_payload: str, config):
    if raw_payload:
        return json.loads(raw_payload)

    if method == "GetDeviceStatus":
        return request_get_status(config, refresh=True)
    if method == "AddDevice":
        prompt_password_if_needed(config)
        return request_add_device(config)
    if method == "ReconnectDevice":
        return request_device_id(config, force=True)
    if method in {"DisconnectDevice", "DeleteDevice"}:
        return request_device_id(config, disconnectFirst=True)
    if method == "SyncPermissions":
        return request_sync_permission(config)
    if method == "SyncPersons":
        face_base64, face_format = read_face_file(config.face_image)
        return request_sync_person(config, face_base64, face_format=face_format)
    if method in {"GetFaces", "DeleteFaces", "DeletePersons"}:
        return request_employee(config)
    if method == "CaptureFaceStream":
        return request_capture(config)
    if method == "GetEnrollmentStatus":
        return request_enrollment_status(config)

    raise ValueError("不支持的方法: " + method)


if __name__ == "__main__":
    sys.exit(main())
