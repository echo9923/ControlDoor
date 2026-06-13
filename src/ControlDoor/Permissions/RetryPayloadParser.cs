using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;
using ControlDoor.Hikvision;

namespace ControlDoor.Permissions
{
    public sealed class RetryPayloadParser
    {
        public int MaxFaceImageBytes { get; set; } = 200 * 1024;

        public PersonInfo ParsePerson(string payloadJson, string employeeId)
        {
            var values = ParseObject(payloadJson);
            var person = new PersonInfo
            {
                EmployeeId = FirstString(values, employeeId, "employee_id", "employeeId", "employee_no", "employeeNo"),
                Name = GetString(values, "name", "full_name", "fullName"),
                CardNumber = GetString(values, "card_number", "cardNumber"),
                Department = GetString(values, "department", "dept"),
                Enabled = GetBool(values, "enabled", "active", "is_active") ?? true,
                ValidFrom = GetDateTime(values, "valid_from", "validFrom"),
                ValidTo = GetDateTime(values, "valid_to", "validTo")
            };

            var gender = GetString(values, "gender", "sex");
            if (!string.IsNullOrWhiteSpace(gender))
            {
                person.Metadata["gender"] = gender;
            }

            return person;
        }

        public FaceInfo ParseFace(string payloadJson, string employeeId)
        {
            var values = ParseObject(payloadJson);
            var base64 = NormalizeBase64(GetString(values, "face_image_base64", "faceImageBase64", "face_base64", "faceBase64", "face_image"));
            var bytes = string.IsNullOrWhiteSpace(base64) ? new byte[0] : Convert.FromBase64String(base64);
            if (bytes.Length > MaxFaceImageBytes)
            {
                throw new InvalidOperationException("人脸图片超过允许大小。");
            }

            return new FaceInfo
            {
                EmployeeId = FirstString(values, employeeId, "employee_id", "employeeId", "employee_no", "employeeNo"),
                CardNumber = GetString(values, "card_number", "cardNumber"),
                FaceId = GetString(values, "face_id", "faceId"),
                ImageBase64 = base64,
                ImageBytes = bytes,
                ImageFormat = GetString(values, "face_image_format", "faceImageFormat") ?? InferFormat(base64)
            };
        }

        public int? ParsePermissionLevel(string payloadJson)
        {
            var values = ParseObject(payloadJson);
            return GetInt(values, "permission_code", "permissionCode", "permission_level", "permissionLevel");
        }

        private static IDictionary<string, object> ParseObject(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var values = serializer.DeserializeObject(payloadJson) as IDictionary<string, object>;
            if (values == null)
            {
                throw new InvalidOperationException("补偿 payload 必须是 JSON 对象。");
            }

            return values;
        }

        private static string FirstString(IDictionary<string, object> values, string fallback, params string[] names)
        {
            var value = GetString(values, names);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static string GetString(IDictionary<string, object> values, params string[] names)
        {
            object value;
            return TryGetValue(values, out value, names) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
        }

        private static int? GetInt(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : (int?)null;
        }

        private static bool? GetBool(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool parsed;
            return bool.TryParse(text, out parsed) ? parsed : (bool?)null;
        }

        private static DateTime? GetDateTime(IDictionary<string, object> values, params string[] names)
        {
            object value;
            if (!TryGetValue(values, out value, names) || value == null)
            {
                return null;
            }

            DateTime parsed;
            return DateTime.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static bool TryGetValue(IDictionary<string, object> values, out object value, params string[] names)
        {
            value = null;
            if (values == null || names == null)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (values.TryGetValue(name, out value))
                {
                    return true;
                }

                foreach (var item in values)
                {
                    if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = item.Value;
                        return true;
                    }
                }
            }

            return false;
        }

        private static string NormalizeBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            var comma = trimmed.IndexOf(',');
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            {
                return trimmed.Substring(comma + 1);
            }

            return trimmed;
        }

        private static string InferFormat(string faceBase64)
        {
            if (string.IsNullOrWhiteSpace(faceBase64) || !faceBase64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return "jpg";
            }

            var slash = faceBase64.IndexOf('/');
            var semicolon = faceBase64.IndexOf(';');
            if (slash >= 0 && semicolon > slash)
            {
                return faceBase64.Substring(slash + 1, semicolon - slash - 1);
            }

            return "jpg";
        }
    }
}
