using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlDoor.Hikvision
{
    public static class HikvisionGatewayValidator
    {
        public static void RequireLoginRequest(LoginRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RequireText(request.IpAddress, "IpAddress");
            RequireText(request.UserName, "UserName");
            RequireText(request.Password, "Password");
            RequirePositive(request.Port, "Port");
            RequirePositive(request.TimeoutMilliseconds, "TimeoutMilliseconds");
        }

        public static void RequireUserId(int userId, string propertyName = "UserId")
        {
            if (userId < 0)
            {
                throw new ArgumentOutOfRangeException(propertyName, "UserId must be greater than or equal to 0.");
            }
        }

        public static void RequireAlarmHandle(int alarmHandle)
        {
            if (alarmHandle < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(alarmHandle), "AlarmHandle must be greater than or equal to 0.");
            }
        }

        public static void RequirePerson(PersonInfo person)
        {
            if (person == null)
            {
                throw new ArgumentNullException(nameof(person));
            }

            RequireText(person.EmployeeId, "EmployeeId");
            RequireText(person.Name, "Name");
        }

        public static void RequireFace(FaceInfo face, int maxImageBytes)
        {
            if (face == null)
            {
                throw new ArgumentNullException(nameof(face));
            }

            RequireText(face.EmployeeId, "EmployeeId");
            var bytes = ResolveFaceBytes(face);
            if (bytes.Length == 0)
            {
                throw new ArgumentException("Face image is required.", nameof(face));
            }

            if (maxImageBytes > 0 && bytes.Length > maxImageBytes)
            {
                throw new DeviceGatewayException("UploadFace", SdkError.FromCode(17, "人脸图片超过设备限制"));
            }

            if (!LooksLikeJpeg(bytes))
            {
                throw new DeviceGatewayException("UploadFace", SdkError.FromCode(17, "仅支持 JPEG 人脸图片"));
            }
        }

        public static void RequirePermissions(IEnumerable<PermissionInfo> permissions)
        {
            if (permissions == null || !permissions.Any())
            {
                throw new ArgumentException("Permission list is required.", nameof(permissions));
            }

            foreach (var permission in permissions)
            {
                if (permission == null)
                {
                    throw new ArgumentException("Permission item cannot be null.", nameof(permissions));
                }

                RequireText(permission.EmployeeId, "EmployeeId");
                RequireText(permission.PermissionCode, "PermissionCode");
                if (permission.DoorIndexes == null || permission.DoorIndexes.Count == 0)
                {
                    throw new ArgumentException("At least one door index is required.", nameof(permissions));
                }

                foreach (var doorIndex in permission.DoorIndexes)
                {
                    RequirePositive(doorIndex, "DoorIndex");
                }
            }
        }

        public static void RequireGateControl(GateControlRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RequireUserId(request.UserId);
            RequirePositive(request.GateIndex, "GateIndex");
            if (!Enum.IsDefined(typeof(GateControlCommand), request.Command))
            {
                throw new ArgumentOutOfRangeException(nameof(request.Command), "Unsupported gate command.");
            }
        }

        public static void RequireCapture(CaptureRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RequireUserId(request.UserId);
            RequirePositive(request.Channel, "Channel");
            RequirePositive(request.TimeoutMilliseconds, "TimeoutMilliseconds");
        }

        public static void RequireIsapiRequest(IsapiRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!Enum.IsDefined(typeof(IsapiMethod), request.Method))
            {
                throw new ArgumentOutOfRangeException(nameof(request.Method), "Unsupported ISAPI method.");
            }

            RequireText(request.Path, "Path");
            RequirePositive(request.TimeoutMilliseconds, "TimeoutMilliseconds");
        }

        public static void RequireDateRange(DateTime beginTime, DateTime endTime)
        {
            if (beginTime == default(DateTime))
            {
                throw new ArgumentException("BeginTime is required.", nameof(beginTime));
            }

            if (endTime == default(DateTime))
            {
                throw new ArgumentException("EndTime is required.", nameof(endTime));
            }

            if (endTime < beginTime)
            {
                throw new ArgumentException("EndTime must be greater than or equal to BeginTime.");
            }
        }

        public static byte[] ResolveFaceBytes(FaceInfo face)
        {
            if (face == null)
            {
                return new byte[0];
            }

            if (face.ImageBytes != null && face.ImageBytes.Length > 0)
            {
                return face.ImageBytes;
            }

            if (!string.IsNullOrWhiteSpace(face.ImageBase64))
            {
                return Convert.FromBase64String(face.ImageBase64);
            }

            return new byte[0];
        }

        public static bool LooksLikeJpeg(byte[] bytes)
        {
            return bytes != null &&
                bytes.Length >= 4 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xD8 &&
                bytes[bytes.Length - 2] == 0xFF &&
                bytes[bytes.Length - 1] == 0xD9;
        }

        private static void RequireText(string value, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(propertyName + " is required.", propertyName);
            }
        }

        private static void RequirePositive(int value, string propertyName)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(propertyName, propertyName + " must be greater than 0.");
            }
        }
    }
}
