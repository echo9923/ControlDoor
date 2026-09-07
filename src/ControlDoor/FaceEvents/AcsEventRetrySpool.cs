using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using ControlDoor.Observability;

namespace ControlDoor.FaceEvents
{
    internal sealed class AcsEventRetrySpool
    {
        private readonly string directory;
        private readonly ServiceLogger logger;
        private readonly Dictionary<RawAcsAlarmEvent, string> paths = new Dictionary<RawAcsAlarmEvent, string>();
        private readonly object gate = new object();

        public AcsEventRetrySpool(string directory, ServiceLogger logger)
        {
            this.directory = directory;
            this.logger = logger;
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        public void Save(RawAcsAlarmEvent item)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            lock (gate)
            {
                if (paths.ContainsKey(item)) return;
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
                var temporary = path + ".tmp";
                var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new StoredEvent
                {
                    Event = item,
                    ReceivedAtBinary = item.ReceivedAt.ToBinary()
                }));
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    File.Move(temporary, path);
                    paths.Add(item, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        public IReadOnlyList<RawAcsAlarmEvent> Load(int count)
        {
            var result = new List<RawAcsAlarmEvent>();
            if (string.IsNullOrWhiteSpace(directory)) return result;
            lock (gate)
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                {
                    if (result.Count >= count) break;
                    if (paths.ContainsValue(path)) continue;
                    try
                    {
                        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                        var json = File.ReadAllText(path, Encoding.UTF8);
                        var stored = serializer.Deserialize<StoredEvent>(json);
                        var item = stored?.Event;
                        if (item == null) throw new InvalidDataException("ACS retry event is empty.");
                        item.ReceivedAt = DateTime.FromBinary(stored.ReceivedAtBinary);
                        var fields = (Dictionary<string, object>)serializer.Deserialize<Dictionary<string, object>>(json)["Event"];
                        if (fields.TryGetValue("Values", out var values) && values is Dictionary<string, object> dictionary)
                        {
                            foreach (var value in dictionary) item.Values[value.Key] = Convert.ToString(value.Value);
                        }
                        paths.Add(item, path);
                        result.Add(item);
                    }
                    catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is InvalidDataException ||
                        ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                    {
                        logger?.Error("FaceEventIngestion", "ACS 重试文件无法解析，已保留为 .invalid 文件: " + path, ex);
                        File.Move(path, path + ".invalid");
                    }
                }
            }
            return result;
        }

        public void Complete(RawAcsAlarmEvent item)
        {
            lock (gate)
            {
                if (!paths.TryGetValue(item, out var path)) return;
                File.Delete(path);
                paths.Remove(item);
            }
        }

        // 死信区：不可自动恢复事件的持久隔离。文件含失败原因，运维确认修复后可将其移回
        // 重试目录（Load 按相同结构解析，额外字段被忽略）即可重放。
        public void DeadLetter(RawAcsAlarmEvent item, string code, string message)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("ACS retry directory is not configured.");
            lock (gate)
            {
                var deadLetterDirectory = Path.Combine(directory, "dead-letter");
                Directory.CreateDirectory(deadLetterDirectory);
                var path = Path.Combine(deadLetterDirectory, Guid.NewGuid().ToString("N") + ".json");
                var temporary = path + ".tmp";
                var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new DeadLetterStoredEvent
                {
                    Event = item,
                    ReceivedAtBinary = item.ReceivedAt.ToBinary(),
                    FailureCode = code,
                    FailureMessage = message
                }));
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }

                if (paths.TryGetValue(item, out var retryPath))
                {
                    try { File.Delete(retryPath); }
                    catch (IOException) { /* 重试文件删除失败不阻断死信写入 */ }
                    paths.Remove(item);
                }
            }
        }

        private sealed class StoredEvent
        {
            public RawAcsAlarmEvent Event { get; set; }

            public long ReceivedAtBinary { get; set; }
        }

        private sealed class DeadLetterStoredEvent
        {
            public RawAcsAlarmEvent Event { get; set; }

            public long ReceivedAtBinary { get; set; }

            public string FailureCode { get; set; }

            public string FailureMessage { get; set; }
        }
    }
}
