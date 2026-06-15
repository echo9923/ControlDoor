import json
import os
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from control_door_grpc_common import (
    DEFAULT_DEVICE_ID,
    DEFAULT_DEVICE_IP,
    DEFAULT_DEVICE_PORT,
    DEFAULT_HOST,
    DEFAULT_USERNAME,
    PASSWORD_ENV,
    TEST_EMPLOYEE_PREFIX,
    TestConfig,
    TestReport,
    build_config_snapshot,
    read_face_file,
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
)


class ControlDoorGrpcGui(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("ControlDoor 真实设备 gRPC 联调")
        self.geometry("1180x760")
        self.queue = queue.Queue()
        self.running = False
        self.last_capture_task_id = ""
        self.report = None
        self._build_widgets()
        self.after(100, self._drain_queue)

    def _build_widgets(self):
        root = ttk.Frame(self, padding=10)
        root.pack(fill=tk.BOTH, expand=True)

        form = ttk.LabelFrame(root, text="连接与测试参数", padding=10)
        form.pack(fill=tk.X)

        self.host = self._entry(form, "gRPC 地址", DEFAULT_HOST, 0, 0)
        self.device_id = self._entry(form, "设备 ID", str(DEFAULT_DEVICE_ID), 0, 2)
        self.device_ip = self._entry(form, "设备 IP", DEFAULT_DEVICE_IP, 0, 4)
        self.device_port = self._entry(form, "SDK 端口", str(DEFAULT_DEVICE_PORT), 0, 6)
        self.username = self._entry(form, "用户名", DEFAULT_USERNAME, 1, 0)
        self.password = self._entry(form, "密码", os.environ.get(PASSWORD_ENV, ""), 1, 2, show="*")
        self.employee_id = self._entry(form, "测试员工", self._new_employee_id(), 1, 4)
        self.permission_code = self._entry(form, "权限码", "1", 1, 6)

        ttk.Label(form, text="人脸图片").grid(row=2, column=0, sticky=tk.W, pady=4)
        self.face_image = tk.StringVar()
        ttk.Entry(form, textvariable=self.face_image, width=58).grid(row=2, column=1, columnspan=5, sticky=tk.EW, padx=(4, 8))
        ttk.Button(form, text="选择", command=self._choose_face).grid(row=2, column=6, sticky=tk.EW)
        ttk.Button(form, text="新测试员工", command=self._reset_employee).grid(row=2, column=7, sticky=tk.EW, padx=(8, 0))

        self.cleanup = tk.BooleanVar(value=True)
        self.delete_device_record = tk.BooleanVar(value=False)
        ttk.Checkbutton(form, text="结束后清理 TEST_CD_ 测试员工", variable=self.cleanup).grid(row=3, column=1, columnspan=2, sticky=tk.W)
        ttk.Checkbutton(form, text="删除设备记录（默认不要勾）", variable=self.delete_device_record).grid(row=3, column=3, columnspan=2, sticky=tk.W)

        for column in range(8):
            form.columnconfigure(column, weight=1)

        buttons = ttk.LabelFrame(root, text="接口调用", padding=10)
        buttons.pack(fill=tk.X, pady=(10, 0))
        button_specs = [
            ("GetDeviceStatus", self.run_single),
            ("AddDevice", self.run_single),
            ("ReconnectDevice", self.run_single),
            ("RearmDeviceAlarm", self.run_single),
            ("DisarmDeviceAlarm", self.run_single),
            ("GetDeviceAlarmStatus", self.run_single),
            ("SyncPersons", self.run_single),
            ("SyncPermissions", self.run_single),
            ("GetFaces", self.run_single),
            ("CaptureFaceStream", self.run_single),
            ("GetEnrollmentStatus", self.run_single),
            ("DeleteFaces", self.run_single),
            ("DeletePersons", self.run_single),
            ("DisconnectDevice", self.run_single),
        ]
        for index, (name, command) in enumerate(button_specs):
            ttk.Button(buttons, text=name, command=lambda n=name: command(n)).grid(row=index // 6, column=index % 6, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(buttons, text="强制全量测试", command=self.run_full).grid(row=2, column=0, columnspan=2, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(buttons, text="保存当前报告", command=self.save_report).grid(row=2, column=2, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(buttons, text="清空输出", command=lambda: self.output.delete("1.0", tk.END)).grid(row=2, column=3, sticky=tk.EW, padx=4, pady=4)
        for column in range(6):
            buttons.columnconfigure(column, weight=1)

        self.output = tk.Text(root, wrap=tk.NONE, height=28)
        self.output.pack(fill=tk.BOTH, expand=True, pady=(10, 0))
        self.status = tk.StringVar(value="就绪")
        ttk.Label(root, textvariable=self.status).pack(fill=tk.X, pady=(6, 0))

    def _entry(self, parent, label, value, row, column, show=None):
        ttk.Label(parent, text=label).grid(row=row, column=column, sticky=tk.W, pady=4)
        var = tk.StringVar(value=value)
        entry = ttk.Entry(parent, textvariable=var, width=18, show=show)
        entry.grid(row=row, column=column + 1, sticky=tk.EW, padx=(4, 8), pady=4)
        return var

    def _new_employee_id(self):
        from datetime import datetime

        return TEST_EMPLOYEE_PREFIX + datetime.now().strftime("%Y%m%d_%H%M%S")

    def _reset_employee(self):
        self.employee_id.set(self._new_employee_id())

    def _choose_face(self):
        path = filedialog.askopenfilename(
            title="选择人脸图片",
            filetypes=[("Image", "*.jpg *.jpeg *.png"), ("All files", "*.*")],
        )
        if path:
            self.face_image.set(path)

    def build_config(self):
        return TestConfig(
            host=self.host.get().strip() or DEFAULT_HOST,
            device_id=int(self.device_id.get().strip() or DEFAULT_DEVICE_ID),
            device_ip=self.device_ip.get().strip() or DEFAULT_DEVICE_IP,
            device_port=int(self.device_port.get().strip() or DEFAULT_DEVICE_PORT),
            username=self.username.get().strip() or DEFAULT_USERNAME,
            password=self.password.get(),
            employee_id=self.employee_id.get().strip(),
            face_image=self.face_image.get().strip(),
            permission_code=int(self.permission_code.get().strip() or 1),
            cleanup=self.cleanup.get(),
            delete_device_record=self.delete_device_record.get(),
            full=True,
        )

    def run_single(self, method):
        if self.running:
            return
        self._start_worker(lambda: self._run_single_worker(method))

    def run_full(self):
        if self.running:
            return
        self._start_worker(self._run_full_worker)

    def _start_worker(self, target):
        self.running = True
        self.status.set("运行中...")
        threading.Thread(target=target, daemon=True).start()

    def _run_single_worker(self, method):
        config = self.build_config()
        report = TestReport(config=build_config_snapshot(config))
        client = None
        try:
            payload = self._payload_for(method, config)
            client = ControlDoorGrpcClient(config.host, config.timeout_seconds, config.api_key)
            record = client.call(method, payload)
            report.add(record)
            report.finish(record.message or record.code)
            if method == "CaptureFaceStream":
                self.last_capture_task_id = self._extract_task_id(record.response)
            self.report = report
            self._post(json.dumps(record.to_dict(), ensure_ascii=False, indent=2))
        except Exception as exc:
            report.success = False
            report.finish(str(exc))
            self.report = report
            self._post("调用失败: " + str(exc))
        finally:
            if client:
                client.close()
            self._post_done()

    def _run_full_worker(self):
        config = self.build_config()
        try:
            report = run_full_smoke(config, log=self._post)
            path = write_report(report, config.report)
            self.report = report
            self._post("报告已保存: " + str(path))
            self._post("结果: " + ("OK" if report.success else "FAILED"))
        except Exception as exc:
            if self.report:
                try:
                    path = write_report(self.report, config.report)
                    self._post("失败报告已保存: " + str(path))
                except Exception:
                    pass
            self._post("全量测试失败: " + str(exc))
        finally:
            self._post_done()

    def _payload_for(self, method, config):
        if method == "GetDeviceStatus":
            return request_get_status(config, refresh=True)
        if method == "AddDevice":
            return request_add_device(config)
        if method == "ReconnectDevice":
            return request_device_id(config, force=True)
        if method == "RearmDeviceAlarm":
            return request_device_id(config, force=True)
        if method == "DisarmDeviceAlarm":
            return request_device_id(config)
        if method == "GetDeviceAlarmStatus":
            return request_device_id(config)
        if method == "DisconnectDevice":
            return request_device_id(config)
        if method == "DeleteDevice":
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
            return request_enrollment_status(config, self.last_capture_task_id)
        raise ValueError("不支持的方法: " + method)

    def _extract_task_id(self, response):
        if isinstance(response, list):
            for item in response:
                if isinstance(item, dict) and item.get("taskId"):
                    return str(item["taskId"])
        return ""

    def save_report(self):
        if not self.report:
            messagebox.showinfo("提示", "当前没有可保存的报告。")
            return
        path = filedialog.asksaveasfilename(
            title="保存报告",
            defaultextension=".json",
            filetypes=[("JSON", "*.json"), ("All files", "*.*")],
        )
        if path:
            write_report(self.report, path)
            messagebox.showinfo("已保存", path)

    def _post(self, text):
        self.queue.put(("log", text))

    def _post_done(self):
        self.queue.put(("done", "就绪"))

    def _drain_queue(self):
        try:
            while True:
                kind, text = self.queue.get_nowait()
                if kind == "log":
                    self.output.insert(tk.END, text + "\n")
                    self.output.see(tk.END)
                elif kind == "done":
                    self.running = False
                    self.status.set(text)
        except queue.Empty:
            pass
        self.after(100, self._drain_queue)


if __name__ == "__main__":
    app = ControlDoorGrpcGui()
    app.mainloop()
