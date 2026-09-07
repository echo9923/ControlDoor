using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace ControlDoor.Observability
{
    public sealed class PayloadLogFormatter
    {
        private static readonly HashSet<string> CredentialFields = new HashSet<string>(
            new[]
            {
                "password",
                "apiKey",
                "api_key",
                "x-api-key",
                "secret",
                "token",
                "accessToken",
                "access_token",
                "refreshToken",
                "refresh_token",
                "connectionString",
                "connection_string",
                "credential",
                "credentials",
                "GrpcManagementApiKey"
            },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> FaceImageFields = new HashSet<string>(
            new[] { "face_image_base64", "faceImageBase64", "face_base64", "faceBase64", "faceImage", "face_image" },
            StringComparer.OrdinalIgnoreCase);

        public string Format(string json, LogOptions options)
        {
            options = options ?? new LogOptions();
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            if (!options.EnableGrpcPayloadLogging)
            {
                return "payload=disabled";
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return "payload=empty";
            }

            object parsed;
            try
            {
                parsed = serializer.DeserializeObject(json);
            }
            catch (Exception ex)
            {
                return "payload=invalidJson length=" + json.Length + " reason=" + ex.GetType().Name;
            }

            string formatted;
            if (string.Equals(options.GrpcPayloadLogMode, "Full", StringComparison.OrdinalIgnoreCase))
            {
                var sanitized = Sanitize(parsed, options);
                formatted = serializer.Serialize(sanitized);
            }
            else
            {
                formatted = BuildSummary(parsed);
            }
            var limit = options.MaxPayloadChars > 0 ? options.MaxPayloadChars : 16384;
            if (formatted.Length <= limit)
            {
                return formatted;
            }
            if (char.IsHighSurrogate(formatted[limit - 1]))
            {
                limit--;
            }
            return formatted.Substring(0, limit) + " [payloadTruncated originalLength=" + json.Length + " sanitizedLength=" + formatted.Length + "]";
        }

        private object Sanitize(object value, LogOptions options)
        {
            if (value is IDictionary<string, object> dictionary)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in dictionary)
                {
                    if (!options.IncludeCredentialFields && CredentialFields.Contains(pair.Key))
                    {
                        result[pair.Key] = "***";
                        continue;
                    }

                    if (!options.IncludeFaceImageBase64 && FaceImageFields.Contains(pair.Key))
                    {
                        var text = pair.Value as string;
                        result[pair.Key] = text == null
                            ? "base64=non-string"
                            : "base64Length=" + text.Length;
                        continue;
                    }

                    result[pair.Key] = Sanitize(pair.Value, options);
                }

                return result;
            }

            if (value is object[] array)
            {
                return array.Select(item => Sanitize(item, options)).ToArray();
            }

            if (value is ArrayList list)
            {
                return list.Cast<object>().Select(item => Sanitize(item, options)).ToArray();
            }

            return value;
        }

        private string BuildSummary(object value)
        {
            if (value is IDictionary<string, object> dictionary)
            {
                var keys = string.Join(",", dictionary.Keys.OrderBy(key => key));
                var counts = dictionary
                    .Where(pair => pair.Value is object[] || pair.Value is ArrayList)
                    .Select(pair => pair.Key + "Count=" + GetCount(pair.Value));
                var countsText = string.Join(" ", counts);
                return ("payloadSummary keys=[" + keys + "] " + countsText).Trim();
            }

            if (value is object[] array)
            {
                return "payloadSummary arrayCount=" + array.Length;
            }

            if (value is ArrayList list)
            {
                return "payloadSummary arrayCount=" + list.Count;
            }

            return "payloadSummary type=" + (value == null ? "null" : value.GetType().Name);
        }

        private static int GetCount(object value)
        {
            if (value is object[] array)
            {
                return array.Length;
            }

            if (value is ArrayList list)
            {
                return list.Count;
            }

            return 0;
        }
    }
}
