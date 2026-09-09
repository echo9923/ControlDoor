using System;
using System.Globalization;
using System.IO;
using System.Linq;
using ControlDoor.Configuration;
using ControlDoor.Observability;

namespace ControlDoor.FaceEvents
{
    public sealed class SnapshotStorage
    {
        private const int MaxSnapshotPathLength = 255;
        private readonly string runDirectory;
        private readonly FaceEventLoggingOptions options;
        private readonly ServiceLogger logger;
        private readonly string rootDirectory;

        public SnapshotStorage(string runDirectory, FaceEventLoggingOptions options = null, ServiceLogger logger = null)
        {
            this.runDirectory = string.IsNullOrWhiteSpace(runDirectory) ? RuntimePaths.GetRunDirectory() : Path.GetFullPath(runDirectory);
            this.options = options ?? new FaceEventLoggingOptions();
            this.logger = logger;
            rootDirectory = ResolveRootDirectory(this.runDirectory, this.options.SnapshotRootDirectory);
            Directory.CreateDirectory(rootDirectory);
        }

        public string RootDirectory => rootDirectory;

        public SnapshotSaveResult Save(AcsFaceEvent faceEvent)
        {
            if (faceEvent == null)
            {
                return SnapshotSaveResult.Failed("INVALID_ARGUMENT", "face event is required");
            }

            if (faceEvent.PictureBytes == null || faceEvent.PictureBytes.Length == 0)
            {
                ApplySnapshotPayload(faceEvent, SnapshotSaveResult.None());
                return SnapshotSaveResult.None();
            }

            if (!LooksLikeJpeg(faceEvent.PictureBytes))
            {
                var result = SnapshotSaveResult.Failed("UNSUPPORTED_FORMAT", "snapshot picture is not a JPEG payload");
                ApplySnapshotPayload(faceEvent, result);
                return result;
            }

            try
            {
                var relativeDirectory = Path.Combine(faceEvent.EventTime.ToString("yyyyMMdd"), SafeSegment(faceEvent.DeviceId.ToString()));
                var absoluteDirectory = Path.Combine(rootDirectory, relativeDirectory);
                Directory.CreateDirectory(absoluteDirectory);

                var fileName = BuildFileName(faceEvent, compact: false);
                // 文件名含全局唯一 EventId：目标已存在说明是同一事件先前尝试的落盘产物
                // （temp+Move 原子写保证已存在文件完整），直接复用路径，避免重试产生孤立图片。
                // 完整与紧凑两种命名方案都必须执行复用检查（复核 F05）。
                if (TryReuseExistingSnapshot(faceEvent, absoluteDirectory, fileName, out var reused))
                {
                    return reused;
                }

                if (!TryResolveSnapshotPath(absoluteDirectory, fileName, out var targetPath, out var snapshotPath))
                {
                    fileName = BuildFileName(faceEvent, compact: true);
                    if (TryReuseExistingSnapshot(faceEvent, absoluteDirectory, fileName, out var compactReused))
                    {
                        return compactReused;
                    }

                    TryResolveSnapshotPath(absoluteDirectory, fileName, out targetPath, out snapshotPath);
                }

                if (string.IsNullOrEmpty(snapshotPath) || snapshotPath.Length > MaxSnapshotPathLength)
                {
                    var result = SnapshotSaveResult.Failed("PATH_TOO_LONG", "snapshot path exceeds " + MaxSnapshotPathLength + " characters");
                    ApplySnapshotPayload(faceEvent, result);
                    return result;
                }

                var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, faceEvent.PictureBytes);
                File.Move(tempPath, targetPath);

                var saved = SnapshotSaveResult.SavedResult(snapshotPath);
                ApplySnapshotPayload(faceEvent, saved);
                return saved;
            }
            catch (Exception ex)
            {
                logger?.Error("SnapshotStorage", "Snapshot save failed.", ex, new LogFields
                {
                    DeviceId = faceEvent.DeviceId > 0 ? (int?)faceEvent.DeviceId : null,
                    EmployeeId = faceEvent.EmployeeId
                });
                var result = SnapshotSaveResult.Failed("WRITE_FAILED", ex.Message);
                ApplySnapshotPayload(faceEvent, result);
                return result;
            }
        }

        internal static string SafeSegment(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();
            foreach (var item in invalid)
            {
                value = value.Replace(item, '_');
            }

            return value.Length == 0 ? "unknown" : value;
        }

        // 历史抓拍清理（复核 R3）：只删除根目录下形如 yyyyMMdd 且日期早于截止日的日期目录内文件，
        // 不触碰非日期命名的目录（运维自建结构）与保留期内的目录；每轮有删除文件数预算，
        // 删除失败的文件保留待下一轮，绝不因清理中断而误删保留期内图片。
        public SnapshotCleanupResult CleanupExpired(DateTime cutoff, int maxFileDeletions)
        {
            var result = new SnapshotCleanupResult();
            if (maxFileDeletions <= 0)
            {
                return result;
            }

            try
            {
                if (!Directory.Exists(rootDirectory))
                {
                    return result;
                }

                var dateDirectories = Directory.EnumerateDirectories(rootDirectory)
                    .Select(path => new { Path = path, Name = Path.GetFileName(path) })
                    .Where(item => IsDateDirectoryName(item.Name))
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .ToList();
                foreach (var directory in dateDirectories)
                {
                    if (result.DeletedFiles >= maxFileDeletions)
                    {
                        break;
                    }

                    var directoryDate = DateTime.ParseExact(directory.Name, "yyyyMMdd", CultureInfo.InvariantCulture);
                    if (directoryDate >= cutoff.Date)
                    {
                        continue;
                    }

                    try
                    {
                        var failedBefore = result.FailedFiles;
                        // 物化文件列表：迭代中删除文件会让惰性枚举器抛异常，导致目录无法收尾移除。
                        foreach (var file in Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories).ToList())
                        {
                            if (result.DeletedFiles >= maxFileDeletions)
                            {
                                break;
                            }

                            try
                            {
                                long length = 0;
                                try
                                {
                                    length = new FileInfo(file).Length;
                                }
                                catch (IOException)
                                {
                                }

                                File.Delete(file);
                                result.DeletedFiles++;
                                result.DeletedBytes += length;
                            }
                            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                result.FailedFiles++;
                                logger?.Warn("SnapshotStorage", "历史抓拍文件暂时无法删除，将在下一轮重试: " + file + "。原因: " + ex.Message);
                            }
                        }

                        // 本目录无失败才尝试移除空目录；移除失败不影响数据安全与后续轮次。
                        if (result.FailedFiles == failedBefore && TryRemoveEmptyDateDirectory(directory.Path))
                        {
                            result.RemovedDirectories++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        result.FailedFiles++;
                        logger?.Warn("SnapshotStorage", "历史抓拍目录暂时无法清理，将在下一轮重试: " + directory.Path + "。原因: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error("SnapshotStorage", "历史抓拍清理异常。", ex);
            }

            return result;
        }

        private static bool IsDateDirectoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length != 8)
            {
                return false;
            }

            DateTime date;
            return DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private bool TryRemoveEmptyDateDirectory(string directory)
        {
            try
            {
                RemoveIfEmpty(directory);
                // 物化子目录列表：迭代中删除目录会让惰性枚举器抛异常。
                foreach (var sub in Directory.EnumerateDirectories(directory).ToList())
                {
                    RemoveIfEmpty(sub);
                }

                RemoveIfEmpty(directory);
                // 目录已删除后不能再枚举（DirectoryNotFoundException），用存在性判断收尾。
                return !Directory.Exists(directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void RemoveIfEmpty(string directory)
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        private static void ApplySnapshotPayload(AcsFaceEvent faceEvent, SnapshotSaveResult result)
        {
            faceEvent.RawPayloadFields["snapshotSaved"] = result.Saved;
            faceEvent.RawPayloadFields["snapshotPath"] = result.SnapshotPath ?? string.Empty;
            faceEvent.RawPayloadFields["snapshotError"] = result.Saved ? string.Empty : (result.ErrorCode + ":" + result.ErrorMessage);
            faceEvent.RawPayloadFields["pictureBytes"] = faceEvent.PictureBytes == null ? 0 : faceEvent.PictureBytes.Length;
            faceEvent.RawPayload = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(faceEvent.RawPayloadFields);
        }

        private static string ResolveRootDirectory(string runDirectory, string configuredRoot)
        {
            configuredRoot = string.IsNullOrWhiteSpace(configuredRoot) ? @"D:\ControlDoorData\snapshots" : configuredRoot.Trim();
            return Path.IsPathRooted(configuredRoot)
                ? Path.GetFullPath(configuredRoot)
                : Path.GetFullPath(Path.Combine(runDirectory, configuredRoot));
        }

        private static bool LooksLikeJpeg(byte[] bytes)
        {
            return bytes != null &&
                bytes.Length >= 4 &&
                bytes[0] == 0xFF &&
                bytes[1] == 0xD8 &&
                bytes[bytes.Length - 2] == 0xFF &&
                bytes[bytes.Length - 1] == 0xD9;
        }

        private static string BuildFileName(AcsFaceEvent faceEvent, bool compact)
        {
            if (compact)
            {
                return faceEvent.EventTime.ToString("HHmmssfff") + "_" + faceEvent.DeviceId + "_" + faceEvent.EventId + ".jpg";
            }

            return faceEvent.EventTime.ToString("yyyyMMddHHmmssfff") +
                "_" + faceEvent.DeviceId +
                "_" + SafeSegment(faceEvent.EmployeeId) +
                "_" + faceEvent.EventId +
                ".jpg";
        }

        private static bool TryReuseExistingSnapshot(AcsFaceEvent faceEvent, string absoluteDirectory, string fileName, out SnapshotSaveResult result)
        {
            result = null;
            try
            {
                var candidatePath = Path.Combine(absoluteDirectory, fileName);
                if (!File.Exists(candidatePath))
                {
                    return false;
                }

                var snapshotPath = NormalizeSnapshotPath(Path.GetFullPath(candidatePath));
                if (snapshotPath.Length > MaxSnapshotPathLength)
                {
                    return false;
                }

                result = SnapshotSaveResult.SavedResult(snapshotPath);
                ApplySnapshotPayload(faceEvent, result);
                return true;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }

        private static bool TryResolveSnapshotPath(string directory, string fileName, out string targetPath, out string snapshotPath)
        {
            targetPath = null;
            snapshotPath = null;
            try
            {
                var candidatePath = Path.Combine(directory, fileName);
                var candidateSnapshotPath = NormalizeSnapshotPath(Path.GetFullPath(candidatePath));
                if (candidateSnapshotPath.Length > MaxSnapshotPathLength)
                {
                    targetPath = candidatePath;
                    snapshotPath = candidateSnapshotPath;
                    return false;
                }

                targetPath = ResolveCollision(candidatePath);
                snapshotPath = NormalizeSnapshotPath(Path.GetFullPath(targetPath));
                return snapshotPath.Length <= MaxSnapshotPathLength;
            }
            catch (PathTooLongException)
            {
                return false;
            }
        }

        private static string ResolveCollision(string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                return targetPath;
            }

            var directory = Path.GetDirectoryName(targetPath);
            var name = Path.GetFileNameWithoutExtension(targetPath);
            var extension = Path.GetExtension(targetPath);
            for (var index = 1; index < 1000; index++)
            {
                var candidate = Path.Combine(directory, name + "_" + index.ToString("x") + extension);
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
        }

        private static string NormalizeSnapshotPath(string path)
        {
            const string extendedPathPrefix = @"\\?\";
            const string extendedUncPrefix = @"\\?\UNC\";
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            if (path.StartsWith(extendedUncPrefix, StringComparison.Ordinal))
            {
                return @"\\" + path.Substring(extendedUncPrefix.Length);
            }

            return path.StartsWith(extendedPathPrefix, StringComparison.Ordinal)
                ? path.Substring(extendedPathPrefix.Length)
                : path;
        }
    }
}
