using System;
using System.Collections.Generic;

namespace ControlDoor.Hikvision
{
    public sealed class SdkError
    {
        private static readonly IDictionary<int, string> CommonMessages = new Dictionary<int, string>
        {
            { 0, "成功" },
            { 1, "用户名或密码错误" },
            { 2, "权限不足" },
            { 3, "SDK 未初始化" },
            { 4, "通道号错误" },
            { 5, "连接设备的用户数超过最大限制" },
            { 7, "连接设备失败" },
            { 17, "参数错误" },
            { 23, "设备不支持该功能" },
            { 29, "设备操作失败" },
            { 41, "设备资源不足" },
            { 43, "缓冲区太小" },
            { 52, "网络通信超时或设备无响应" },
            { 401, "ISAPI 认证失败" },
            { 403, "ISAPI 权限不足" },
            { 404, "ISAPI 资源不存在" },
            { 408, "ISAPI 请求超时" },
            { 500, "ISAPI 设备内部错误" }
        };

        public int Code { get; set; }

        public string Message { get; set; }

        public string Source { get; set; }

        public bool Success => Code == 0;

        internal bool Retryable
        {
            get
            {
                if (string.Equals(Source, "ISAPI", StringComparison.OrdinalIgnoreCase))
                {
                    // ISAPI status 2 is device busy; SDK error 2 is permission denied.
                    return Code == 2 || Code == 3 || Code == 408 || Code == 429 ||
                        Code == 500 || Code == 502 || Code == 503 || Code == 504;
                }

                if (string.Equals(Source, "SDK_REMOTE_CONFIG", StringComparison.OrdinalIgnoreCase))
                {
                    return Code == 1001 || Code == 1004;
                }

                return Code == 7 || Code == 41 || Code == 43 || Code == 52 || Code == 408 || Code == 500;
            }
        }

        internal bool DeviceUnsupported => Code == 23 &&
            !string.Equals(Source, "ISAPI", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Source, "SDK_REMOTE_CONFIG", StringComparison.OrdinalIgnoreCase);

        public static SdkError Ok()
        {
            return FromCode(0);
        }

        public static SdkError FromCode(int code, string message = null, string source = "SDK")
        {
            return new SdkError
            {
                Code = code,
                Message = string.IsNullOrWhiteSpace(message) ? GetDefaultMessage(code) : message,
                Source = string.IsNullOrWhiteSpace(source) ? "SDK" : source
            };
        }

        public static SdkError FromHttpStatusCode(int statusCode, string message = null)
        {
            return FromCode(statusCode, message, "ISAPI");
        }

        public static SdkError FromException(Exception exception, string source = "Gateway")
        {
            if (exception == null)
            {
                return FromCode(-1, "未知异常", source);
            }

            return FromCode(-1, exception.Message, source);
        }

        public static string GetDefaultMessage(int code)
        {
            string message;
            if (CommonMessages.TryGetValue(code, out message))
            {
                return message;
            }

            if (code >= 400 && code <= 599)
            {
                return "ISAPI HTTP 状态码 " + code;
            }

            return "未知 SDK 错误码 " + code;
        }
    }
}
