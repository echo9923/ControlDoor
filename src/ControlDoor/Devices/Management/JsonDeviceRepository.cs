using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using ControlDoor.Configuration;
using ControlDoor.Observability;

namespace ControlDoor.Devices.Management
{
    // 基于 JSON 文件的 IDeviceRepository 实现。
    //
    // 设计要点：
    //   * 启动期一次性加载 devices.json（或 AppSettings.Devices.Items 内联清单）到内存缓存，
    //     后续读操作全部走缓存，避免高频磁盘 IO。
    //   * InsertDevice / DeleteDevice 在锁内更新缓存并原子写回（写临时文件 + File.Replace），
    //     写前按配置备份到 *.bak，避免运行时增删设备损坏清单。
    //   * 字段映射：DeviceStoreItem <-> DeviceRecord，types 字符串列表 <-> DeviceType 枚举。
    //   * devices.json 不支持注释（与 appsettings.json 不同），文件较小且单独维护。
    public sealed class JsonDeviceRepository : IDeviceRepository
    {
        private readonly object fileGate = new object();
        private readonly string filePath;
        private readonly bool backupOnWrite;
        private readonly ServiceLogger logger;
        private readonly bool useInlineItems;
        private List<DeviceRecord> cache;

        public JsonDeviceRepository(string runDirectory, DeviceStoreOptions options, ServiceLogger logger = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(runDirectory))
            {
                throw new ArgumentNullException(nameof(runDirectory));
            }

            this.logger = logger;
            backupOnWrite = options.BackupOnWrite;

            // 内联清单优先：若 Items 非空，直接以配置中的清单为数据源，不读写文件。
            // 这便于测试和临时小规模部署把设备直接写在 appsettings.json 里。
            useInlineItems = options.Items != null && options.Items.Count > 0;
            if (useInlineItems)
            {
                filePath = null;
                cache = options.Items.Select(MapToRecord).ToList();
                return;
            }

            filePath = Path.IsPathRooted(options.FilePath)
                ? options.FilePath
                : Path.Combine(runDirectory, options.FilePath ?? string.Empty);

            cache = LoadFromFile();
        }

        public IReadOnlyList<DeviceRecord> LoadEnabledDevices()
        {
            lock (fileGate)
            {
                return cache
                    .Where(item => item.Enabled)
                    .OrderBy(item => item.DeviceId)
                    .Select(Clone)
                    .ToList();
            }
        }

        public IReadOnlyList<DeviceRecord> LoadAllDevices()
        {
            lock (fileGate)
            {
                return cache
                    .OrderBy(item => item.DeviceId)
                    .Select(Clone)
                    .ToList();
            }
        }

        public DeviceRecord GetByDeviceId(int deviceId)
        {
            lock (fileGate)
            {
                var match = cache.FirstOrDefault(item => item.DeviceId == deviceId);
                return match == null ? null : Clone(match);
            }
        }

        public bool ExistsDeviceId(int deviceId)
        {
            lock (fileGate)
            {
                return cache.Any(item => item.DeviceId == deviceId);
            }
        }

        public bool ExistsIpAddress(string ipAddress)
        {
            lock (fileGate)
            {
                var normalized = NormalizeString(ipAddress);
                return cache.Any(item => string.Equals(NormalizeString(item.IpAddress), normalized, StringComparison.OrdinalIgnoreCase));
            }
        }

        public DeviceStoreWriteResult InsertDevice(DeviceRecord record)
        {
            if (record == null)
            {
                return DeviceStoreWriteResult.Failed("INVALID_ARGUMENT", "Device record is required.");
            }

            lock (fileGate)
            {
                if (cache.Any(item => item.DeviceId == record.DeviceId))
                {
                    return DeviceStoreWriteResult.Failed("DUPLICATE_DEVICE_ID", "deviceId=" + record.DeviceId + " 已存在。");
                }

                if (cache.Any(item => string.Equals(NormalizeString(item.IpAddress), NormalizeString(record.IpAddress), StringComparison.OrdinalIgnoreCase)))
                {
                    return DeviceStoreWriteResult.Failed("DUPLICATE_IP_ADDRESS", "ipAddress=" + record.IpAddress + " 已存在。");
                }

                var cloned = Clone(record);
                cache.Add(cloned);
                var writeError = Persist();
                if (writeError != null)
                {
                    // 写回失败时回滚缓存，避免内存态与持久态不一致。
                    cache.Remove(cloned);
                    return DeviceStoreWriteResult.Failed("WRITE_FAILED", writeError);
                }

                return DeviceStoreWriteResult.Ok(1, "InsertDevice succeeded.");
            }
        }

        public DeviceStoreWriteResult DeleteDevice(int deviceId)
        {
            lock (fileGate)
            {
                if (!cache.Any(item => item.DeviceId == deviceId))
                {
                    // 删除不存在的行视为成功 0 行，保持设备管理 gRPC 的幂等语义。
                    return DeviceStoreWriteResult.Ok(0, "DeleteDevice succeeded (no rows affected).");
                }

                var removed = cache.Where(item => item.DeviceId == deviceId).Select(Clone).ToList();
                cache.RemoveAll(item => item.DeviceId == deviceId);
                var writeError = Persist();
                if (writeError != null)
                {
                    cache.AddRange(removed);
                    cache = cache.OrderBy(item => item.DeviceId).ToList();
                    return DeviceStoreWriteResult.Failed("WRITE_FAILED", writeError);
                }

                return DeviceStoreWriteResult.Ok(1, "DeleteDevice succeeded.");
            }
        }

        // 启动期从文件加载设备清单。文件必须存在，缺失会让 Host 启动失败。
        private List<DeviceRecord> LoadFromFile()
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                var message = "设备清单文件不存在: " + (filePath ?? "<null>") + "。请创建 Configuration/devices.json 或在 Devices.Items 中提供临时内联清单。";
                logger?.Error("DeviceStore", message, null);
                throw new FileNotFoundException(message, filePath);
            }

            try
            {
                var json = File.ReadAllText(filePath, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var document = serializer.Deserialize<DeviceStoreDocument>(json) ?? new DeviceStoreDocument();
                var records = (document.devices ?? new List<DeviceStoreItem>())
                    .Select((item, index) => MapToRecord(item, "devices[" + index + "]"))
                    .ToList();

                logger?.Info("DeviceStore", "设备清单加载完成。", new LogFields
                {
                    Extra = { ["count"] = records.Count.ToString(), ["filePath"] = filePath }
                });
                return records;
            }
            catch (Exception ex)
            {
                logger?.Error("DeviceStore", "设备清单加载失败。", ex);
                throw new InvalidOperationException("设备清单加载失败: " + ex.Message, ex);
            }
        }

        // 把当前缓存写回 devices.json。原子写：先写 *.tmp，再 File.Replace 覆盖原文件。
        // 返回 null 表示成功；非 null 表示错误描述（已记录日志）。
        // 内联清单模式下跳过写盘（数据源是 appsettings.json，由运维手动维护）。
        private string Persist()
        {
            if (useInlineItems || string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (backupOnWrite && File.Exists(filePath))
                {
                    var backupPath = filePath + ".bak";
                    // File.Copy 的 overwrite=true 在目标存在时会覆盖，避免备份失败中断写入。
                    File.Copy(filePath, backupPath, overwrite: true);
                }

                var document = new Dictionary<string, object>
                {
                    ["devices"] = cache.OrderBy(item => item.DeviceId).Select(MapToItem).ToList()
                };

                var serializer = new JavaScriptSerializer();
                var json = serializer.Serialize(document);
                json = Prettify(json);

                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json, Encoding.UTF8);

                ReplaceFile(tempPath, filePath);

                return null;
            }
            catch (Exception ex)
            {
                logger?.Error("DeviceStore", "设备清单写回失败。", ex);
                return ex.Message;
            }
        }

        // 简单的 JSON 美化，避免引入额外依赖。
        // 不依赖 Newtonsoft.Json，与项目现有 JavaScriptSerializer 选择保持一致。
        private static string Prettify(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            var builder = new StringBuilder(json.Length + json.Length / 3);
            var indent = 0;
            var inString = false;
            var escaped = false;

            foreach (var ch in json)
            {
                if (inString)
                {
                    builder.Append(ch);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    builder.Append(ch);
                }
                else if (ch == '{' || ch == '[')
                {
                    builder.Append(ch);
                    builder.AppendLine();
                    indent++;
                    AppendIndent(builder, indent);
                }
                else if (ch == ',')
                {
                    builder.Append(ch);
                    builder.AppendLine();
                    AppendIndent(builder, indent);
                }
                else if (ch == '}' || ch == ']')
                {
                    builder.AppendLine();
                    indent = Math.Max(0, indent - 1);
                    AppendIndent(builder, indent);
                    builder.Append(ch);
                }
                else if (ch == ':')
                {
                    builder.Append(": ");
                }
                else
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString() + Environment.NewLine;
        }

        private static void ReplaceFile(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // 部分 Windows/同步盘/沙箱环境不允许 File.Replace，回退到同目录 rename 覆盖。
            }
            catch (IOException)
            {
                // 目标文件系统不支持 Replace 时同样回退。
            }

            var oldPath = targetPath + ".old";
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }

            File.Move(targetPath, oldPath);
            try
            {
                File.Move(tempPath, targetPath);
            }
            catch
            {
                if (File.Exists(oldPath) && !File.Exists(targetPath))
                {
                    File.Move(oldPath, targetPath);
                }

                throw;
            }
            finally
            {
                if (File.Exists(oldPath))
                {
                    File.Delete(oldPath);
                }
            }
        }

        private static void AppendIndent(StringBuilder builder, int indent)
        {
            builder.Append(new string(' ', indent * 2));
        }

        private static DeviceRecord MapToRecord(DeviceStoreItem item)
        {
            return MapToRecord(item, "Devices.Items");
        }

        private static DeviceRecord MapToRecord(DeviceStoreItem item, string source)
        {
            if (item == null)
            {
                throw new InvalidOperationException(source + " 不能为空。");
            }

            return new DeviceRecord
            {
                DeviceId = item.DeviceId,
                DeviceName = NormalizeString(item.Name),
                Description = item.Remark ?? string.Empty,
                IpAddress = NormalizeString(item.IpAddress),
                Port = item.Port == 0 ? 8000 : item.Port,
                Username = string.IsNullOrWhiteSpace(item.Username) ? "admin" : item.Username,
                Password = item.Password ?? string.Empty,
                Enabled = item.Enabled,
                Types = ParseTypes(item.Types, source + ".types")
            };
        }

        private static Dictionary<string, object> MapToItem(DeviceRecord record)
        {
            return new Dictionary<string, object>
            {
                ["deviceId"] = record.DeviceId,
                ["name"] = NormalizeString(record.DeviceName),
                ["types"] = (record.Types ?? Enumerable.Empty<DeviceType>()).Select(item => item.ToString()).ToList(),
                ["ipAddress"] = NormalizeString(record.IpAddress),
                ["port"] = record.Port,
                ["username"] = string.IsNullOrWhiteSpace(record.Username) ? "admin" : record.Username,
                ["password"] = record.Password ?? string.Empty,
                ["enabled"] = record.Enabled,
                ["remark"] = record.Description ?? string.Empty
            };
        }

        private static List<DeviceType> ParseTypes(IList<string> raw, string source)
        {
            var result = new List<DeviceType>();
            if (raw == null)
            {
                throw new InvalidOperationException(source + " 不能为空（至少声明一个: Acs / FaceCapture / Camera）。");
            }

            foreach (var value in raw)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var parsed = ParseDeviceTypeLiteral(value.Trim());
                if (!parsed.HasValue)
                {
                    throw new InvalidOperationException(source + " 含非法值 \"" + value + "\"（合法值: Acs / FaceCapture / Camera）。");
                }

                if (!result.Contains(parsed.Value))
                {
                    result.Add(parsed.Value);
                }
            }

            if (result.Count == 0)
            {
                throw new InvalidOperationException(source + " 不能为空（至少声明一个: Acs / FaceCapture / Camera）。");
            }

            return result;
        }

        private static DeviceType? ParseDeviceTypeLiteral(string value)
        {
            if (string.Equals(value, "Acs", StringComparison.OrdinalIgnoreCase))
            {
                return DeviceType.Acs;
            }

            if (string.Equals(value, "FaceCapture", StringComparison.OrdinalIgnoreCase))
            {
                return DeviceType.FaceCapture;
            }

            if (string.Equals(value, "Camera", StringComparison.OrdinalIgnoreCase))
            {
                return DeviceType.Camera;
            }

            return null;
        }

        private static DeviceRecord Clone(DeviceRecord source)
        {
            return new DeviceRecord
            {
                DeviceId = source.DeviceId,
                DeviceName = source.DeviceName,
                Description = source.Description,
                IpAddress = source.IpAddress,
                Port = source.Port,
                Username = source.Username,
                Password = source.Password,
                Enabled = source.Enabled,
                Types = (source.Types ?? Enumerable.Empty<DeviceType>()).ToList()
            };
        }

        private static string NormalizeString(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        // devices.json 的顶层结构。字段名 devices（小写）与样例 JSON 保持一致。
        private sealed class DeviceStoreDocument
        {
            public List<DeviceStoreItem> devices { get; set; } = new List<DeviceStoreItem>();
        }
    }
}
