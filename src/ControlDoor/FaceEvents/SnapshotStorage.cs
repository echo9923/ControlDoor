using System;
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
                if (!TryResolveSnapshotPath(absoluteDirectory, fileName, out var targetPath, out var snapshotPath))
                {
                    fileName = BuildFileName(faceEvent, compact: true);
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
            configuredRoot = string.IsNullOrWhiteSpace(configuredRoot) ? "snapshots" : configuredRoot.Trim();
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
