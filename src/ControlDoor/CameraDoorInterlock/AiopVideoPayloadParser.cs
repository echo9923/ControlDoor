using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace ControlDoor.CameraDoorInterlock
{
    /// <summary>
    /// 按 NET_AIOP_VIDEO_HEAD 解析海康 SDK 回调 0x4021 / COMM_UPLOAD_AIOP_VIDEO 的原始 buffer。
    /// 字段偏移依据 docs/海康AIOP短衣短裤报警SDK布防回调说明.md §6/§7：
    ///   0  dwSize/HeaderLen(uint)
    ///   4  dwChannel(uint)
    ///   24 taskID(16 ASCII)
    ///   88 dwAIOPDataSize/JsonLen(uint)
    ///   92 dwPictureSize/PicLen(uint)
    /// 布局：[Header][AIOP JSON][JPEG]。
    /// 本解析器永不抛出——任何异常或校验失败都返回 ParseSucceeded=false 的记录，
    /// 因为 task02 要求 JSON 解析失败仍触发联动。
    /// </summary>
    public sealed class AiopVideoPayloadParser
    {
        public const int CommUploadAiopVideo = 0x4021;

        private const int TaskIdOffset = 24;
        private const int TaskIdLength = 16;
        private const int JsonLengthOffset = 88;
        private const int PictureLengthOffset = 92;
        private const int MinimumHeaderLength = 96;

        private readonly bool logFullJson;

        public AiopVideoPayloadParser(bool logFullJson = false)
        {
            this.logFullJson = logFullJson;
        }

        public AiopVideoPayload Parse(byte[] rawPayload, int cameraDeviceId, string cameraIp)
        {
            var payload = new AiopVideoPayload
            {
                Command = CommUploadAiopVideo,
                CameraDeviceId = cameraDeviceId,
                CameraIp = cameraIp ?? string.Empty
            };

            if (rawPayload == null || rawPayload.Length < MinimumHeaderLength)
            {
                payload.ParseSucceeded = false;
                payload.ParseError = "AIOP buffer 长度不足，无法读取头部字段。";
                payload.JsonLength = 0;
                payload.ImageLength = rawPayload == null ? 0 : rawPayload.Length;
                return payload;
            }

            try
            {
                uint headerLen = BitConverter.ToUInt32(rawPayload, 0);
                uint jsonLen = BitConverter.ToUInt32(rawPayload, JsonLengthOffset);
                uint picLen = BitConverter.ToUInt32(rawPayload, PictureLengthOffset);

                payload.JsonLength = (int)jsonLen;
                payload.ImageLength = (int)picLen;
                payload.TaskId = ReadAscii(rawPayload, TaskIdOffset, TaskIdLength);

                if (headerLen < MinimumHeaderLength || headerLen > rawPayload.Length)
                {
                    payload.ParseSucceeded = false;
                    payload.ParseError = "AIOP 头部长度非法: " + headerLen + "。";
                    return payload;
                }

                long declaredTotal = (long)headerLen + jsonLen + picLen;
                if (declaredTotal != rawPayload.Length)
                {
                    payload.ParseSucceeded = false;
                    payload.ParseError = "AIOP 头部+JSON+图片长度(" + declaredTotal + ")与 buffer 长度(" + rawPayload.Length + ")不一致。";
                    return payload;
                }

                int jsonOffset = (int)headerLen;
                int picOffset = jsonOffset + (int)jsonLen;

                if (jsonLen > 0)
                {
                    payload.JsonText = logFullJson ? Encoding.UTF8.GetString(rawPayload, jsonOffset, (int)jsonLen) : "<redacted>";
                    ExtractJsonFields(payload, rawPayload, jsonOffset, (int)jsonLen);
                }

                if (picLen > 0)
                {
                    payload.ImageIsJpeg = HasJpegSignature(rawPayload, picOffset, (int)picLen);
                }

                payload.ParseSucceeded = true;
                return payload;
            }
            catch (Exception ex)
            {
                payload.ParseSucceeded = false;
                payload.ParseError = "AIOP 解析异常: " + ex.GetType().Name + " - " + ex.Message;
                return payload;
            }
        }

        private static string ReadAscii(byte[] bytes, int offset, int length)
        {
            var end = Math.Min(offset + length, bytes.Length);
            var sb = new StringBuilder();
            for (var i = offset; i < end; i++)
            {
                var b = bytes[i];
                if (b == 0)
                {
                    break;
                }

                sb.Append((char)b);
            }

            return sb.ToString().Trim();
        }

        private static bool HasJpegSignature(byte[] bytes, int offset, int length)
        {
            if (length < 3 || offset < 0 || offset + 3 > bytes.Length)
            {
                return false;
            }

            return bytes[offset] == 0xFF && bytes[offset + 1] == 0xD8 && bytes[offset + 2] == 0xFF;
        }

        private static void ExtractJsonFields(AiopVideoPayload payload, byte[] rawPayload, int jsonOffset, int jsonLen)
        {
            Dictionary<string, object> root;
            try
            {
                var json = Encoding.UTF8.GetString(rawPayload, jsonOffset, jsonLen);
                root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception)
            {
                return;
            }

            if (root == null)
            {
                return;
            }

            var types = new HashSet<string>();
            var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectFromTargets(root, "targets", types, modelIds, payload);
            CollectFromEvents(root, types, modelIds, payload);

            foreach (var value in types)
            {
                payload.DetectedTypes.Add(value);
            }

            foreach (var modelId in modelIds)
            {
                if (string.IsNullOrEmpty(payload.ModelId))
                {
                    payload.ModelId = modelId;
                    break;
                }
            }
        }

        private static void CollectFromTargets(Dictionary<string, object> root, string key, HashSet<string> types, HashSet<string> modelIds, AiopVideoPayload payload)
        {
            if (!root.TryGetValue(key, out object array) || !(array is IEnumerable items))
            {
                return;
            }

            foreach (var item in items)
            {
                var node = item as Dictionary<string, object>;
                if (node == null)
                {
                    continue;
                }

                var obj = TryGetDictionary(node, "obj");
                if (obj != null)
                {
                    RecordTypeAndModel(obj, types, modelIds);
                }
            }
        }

        private static void CollectFromEvents(Dictionary<string, object> root, HashSet<string> types, HashSet<string> modelIds, AiopVideoPayload payload)
        {
            var events = TryGetDictionary(root, "events");
            if (events == null)
            {
                return;
            }

            if (!events.TryGetValue("alertInfo", out object array) || !(array is IEnumerable items))
            {
                return;
            }

            foreach (var item in items)
            {
                var node = item as Dictionary<string, object>;
                if (node == null)
                {
                    continue;
                }

                var target = TryGetDictionary(node, "target");
                if (target != null)
                {
                    RecordTypeAndModel(target, types, modelIds);
                }
            }
        }

        private static void RecordTypeAndModel(Dictionary<string, object> obj, HashSet<string> types, HashSet<string> modelIds)
        {
            if (obj.TryGetValue("type", out object typeValue) && typeValue != null)
            {
                types.Add(ConvertToString(typeValue));
            }

            if (obj.TryGetValue("modelID", out object modelValue) && modelValue != null)
            {
                var modelId = ConvertToString(modelValue);
                if (!string.IsNullOrWhiteSpace(modelId))
                {
                    modelIds.Add(modelId);
                }
            }
        }

        private static Dictionary<string, object> TryGetDictionary(Dictionary<string, object> parent, string key)
        {
            object value;
            if (parent.TryGetValue(key, out value) && value is Dictionary<string, object> dict)
            {
                return dict;
            }

            return null;
        }

        private static string ConvertToString(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is bool b)
            {
                return b ? "true" : "false";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
