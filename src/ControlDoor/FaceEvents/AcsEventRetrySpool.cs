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
        // 反向登记（复核 J2）：磁盘路径 → 内存事件。死信遗留源文件补清理成功后据此解除事件引用，
        // 避免已死信事件的图片字节被字典长期滞留；同时让 Load 的"已登记"检查从 O(n) 降为 O(1)。
        private readonly Dictionary<string, RawAcsAlarmEvent> eventsByPath = new Dictionary<string, RawAcsAlarmEvent>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();

        public AcsEventRetrySpool(string directory, ServiceLogger logger)
        {
            this.directory = directory;
            this.logger = logger;
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        internal int TrackedEventCount
        {
            get
            {
                lock (gate)
                {
                    return paths.Count;
                }
            }
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
                    eventsByPath[path] = item;
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
                IEnumerable<string> files;
                try
                {
                    // 惰性枚举（复核 J3）：达到本批预算即停止遍历，不再为了死信配对扫描整个重试目录；
                // 死信遗留文件的补清理由 Patrol 巡检独立负责。
                files = Directory.EnumerateFiles(directory, "*.json");
                }
                catch (IOException ex)
                {
                    // 目录级瞬时 I/O 故障：本轮放弃加载即可，已交付事件不受影响，下一轮重试。
                    logger?.Warn("FaceEventIngestion", "ACS 重试目录暂时无法枚举，本轮跳过磁盘回放。原因: " + ex.Message);
                    return result;
                }

                try
                {
                    foreach (var path in files)
                    {
                        // 预算已满立即结束：积压数千文件时每批只读取需要的数量。
                        if (result.Count >= count) break;

                        // 已死信但源文件清理未完成的遗留文件：磁盘上的 dl- 配对文件可自描述关联，
                        // 绝不重新投递业务，只做补清理（复核 I3）。
                        if (File.Exists(GetDeadLetterPathForSource(path)))
                        {
                            TryDeleteLeftoverRetryFile(path);
                            continue;
                        }

                        if (eventsByPath.ContainsKey(path)) continue;
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
                            eventsByPath[path] = item;
                            result.Add(item);
                        }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is InvalidDataException ||
                            ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
                        {
                            logger?.Error("FaceEventIngestion", "ACS 重试文件无法解析，已保留为 .invalid 文件: " + path, ex);
                            try
                            {
                                File.Move(path, path + ".invalid");
                            }
                            catch (IOException moveError)
                            {
                                // 隔离移动本身失败（如文件被占用）不得中断整批：保留原文件，下一轮重新判定。
                                logger?.Warn("FaceEventIngestion", "ACS 无效重试文件暂时无法隔离，将在下一轮重试: " + path + "。原因: " + moveError.Message);
                            }
                        }
                        catch (IOException ex)
                        {
                            // 瞬时 I/O 故障（如文件被占用）：跳过该文件，本轮其余事件照常交付（复核 I2）。
                            // 文件保留原状由下一轮 Load 重试，绝不按 JSON 损坏处理为永久无效文件。
                            logger?.Warn("FaceEventIngestion", "ACS 重试文件暂时无法读取，将在下一轮重试: " + path + "。原因: " + ex.Message);
                        }
                    }
                }
                catch (IOException ex)
                {
                    // 枚举中途目录发生变化（.NET Framework 枚举器可中途抛出）：保留已加载事件交付，下一轮继续。
                    logger?.Warn("FaceEventIngestion", "ACS 重试目录枚举中断，本轮已加载事件照常交付。原因: " + ex.Message);
                }
            }
            return result;
        }

        // 死信巡检（复核 J3/R2）：与回放解耦、有预算的独立巡检。补清理死信遗留源文件，
        // 并统计死信目录当前文件数供监控告警；.examined 控制单轮最多检查的文件数。
        public DeadLetterPatrolResult Patrol(int maxFiles)
        {
            var result = new DeadLetterPatrolResult();
            if (string.IsNullOrWhiteSpace(directory)) return result;
            lock (gate)
            {
                var deadLetterDirectory = Path.Combine(directory, "dead-letter");
                try
                {
                    result.DeadLetterCount = Directory.Exists(deadLetterDirectory)
                        ? Directory.EnumerateFiles(deadLetterDirectory, "*.json").Count()
                        : 0;
                }
                catch (IOException ex)
                {
                    logger?.Warn("FaceEventIngestion", "死信目录暂时无法统计，本轮跳过。原因: " + ex.Message);
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*.json");
                }
                catch (IOException ex)
                {
                    logger?.Warn("FaceEventIngestion", "ACS 重试目录暂时无法枚举，本轮跳过死信巡检。原因: " + ex.Message);
                    return result;
                }

                try
                {
                    foreach (var path in files)
                    {
                        if (maxFiles > 0 && result.Examined >= maxFiles) break;
                        result.Examined++;
                        if (File.Exists(GetDeadLetterPathForSource(path)))
                        {
                            if (TryDeleteLeftoverRetryFile(path))
                            {
                                result.LeftoverCleaned++;
                            }
                            else
                            {
                                result.LeftoverPending++;
                            }
                        }
                    }
                }
                catch (IOException ex)
                {
                    logger?.Warn("FaceEventIngestion", "ACS 重试目录枚举中断，本轮死信巡检提前结束。原因: " + ex.Message);
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
                eventsByPath.Remove(path);
            }
        }

        // 死信区：不可自动恢复事件的持久隔离。文件含失败原因，运维确认修复后可通过
        // --replay-dead-letters（或手工把文件移回重试目录）重放。
        // 死信文件以 "dl-" + 源重试文件名 与源文件配对：同一事件重复转移复用同一目标（幂等，
        // 复核 I3）；源文件删除失败时保留内存登记，Patrol 依据配对文件识别"已死信待清理"，
        // 绝不把该源文件重新暴露为普通待处理事件，占用解除后由巡检补清理。
        public void DeadLetter(RawAcsAlarmEvent item, string code, string message)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("ACS retry directory is not configured.");
            lock (gate)
            {
                var deadLetterDirectory = Path.Combine(directory, "dead-letter");
                Directory.CreateDirectory(deadLetterDirectory);
                // 无磁盘源文件（此前 Save 失败的纯内存事件）退回随机名：没有源文件就没有重放与清理关联。
                string retryPath = null;
                if (!paths.TryGetValue(item, out retryPath)) retryPath = null;
                var path = retryPath == null
                    ? Path.Combine(deadLetterDirectory, Guid.NewGuid().ToString("N") + ".json")
                    : GetDeadLetterPathForSource(retryPath);
                if (!File.Exists(path))
                {
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
                }

                if (retryPath == null) return;
                try
                {
                    File.Delete(retryPath);
                    paths.Remove(item);
                    eventsByPath.Remove(retryPath);
                }
                catch (IOException ex)
                {
                    // 死信隔离已完成，仅源文件清理受阻：保留登记使 Load 与 PersistPending 都不再
                    // 触碰该事件；磁盘 dl- 配对文件保证重启后同样不会重放。占用解除后由 Patrol
                    // 补清理并同步解除登记（复核 J2）。
                    logger?.Warn("FaceEventIngestion", "死信源重试文件暂时无法删除，保留待后续补清理: " + retryPath + "。原因: " + ex.Message);
                }
            }
        }

        private string GetDeadLetterPathForSource(string retryPath)
        {
            return Path.Combine(directory, "dead-letter", "dl-" + Path.GetFileName(retryPath));
        }

        private bool TryDeleteLeftoverRetryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                // 死信遗留源文件仍被占用：本轮清理失败不打断巡检，下一轮继续尝试。
                logger?.Warn("FaceEventIngestion", "死信遗留重试文件暂时无法清理，将在下一轮重试: " + path + "。原因: " + ex.Message);
                return false;
            }

            // 补清理成功必须同步解除登记（复核 J2）：paths 以事件对象为键持有图片字节，
            // 只删文件不解除引用会让已死信事件的内存长期滞留。
            if (eventsByPath.TryGetValue(path, out var item))
            {
                eventsByPath.Remove(path);
                paths.Remove(item);
            }

            return true;
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

    internal sealed class DeadLetterPatrolResult
    {
        public int DeadLetterCount { get; set; }

        public int LeftoverCleaned { get; set; }

        public int LeftoverPending { get; set; }

        public int Examined { get; set; }
    }
}
