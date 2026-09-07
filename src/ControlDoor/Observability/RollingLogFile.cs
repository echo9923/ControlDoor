using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ControlDoor.Observability
{
    // Called under ServiceLogger's lock; each output owns its rolling and failure state.
    internal sealed class RollingLogFile
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly string directory;
        private readonly string prefix;
        private readonly Regex ownedName;
        private readonly int retentionDays;
        private readonly long maxFileBytes;
        private readonly long maxTotalBytes;
        private string currentPath;
        private DateTime currentDate;
        private DateTime nextCleanup;
        private bool failureReported;
        private long totalBytes;

        internal RollingLogFile(string directory, string prefix, int retentionDays, long maxFileBytes, long maxTotalBytes)
        {
            this.directory = directory;
            this.prefix = prefix;
            this.retentionDays = Math.Max(1, retentionDays);
            this.maxTotalBytes = Math.Max(256, maxTotalBytes);
            this.maxFileBytes = Math.Max(256, Math.Min(maxFileBytes, this.maxTotalBytes));
            ownedName = new Regex("^" + Regex.Escape(prefix) + @"(?<date>\d{8})(?:-(?<index>\d{3,}))?\.log$", RegexOptions.CultureInvariant);
        }

        internal string CurrentPath(DateTime now)
        {
            return currentPath != null && currentDate == now.Date
                ? currentPath
                : Path.Combine(directory, prefix + now.ToString("yyyyMMdd") + ".log");
        }

        internal void Initialize(DateTime now)
        {
            Try(() =>
            {
                Directory.CreateDirectory(directory);
                SelectCurrentFile(now);
                Cleanup(now);
            });
        }

        internal void Append(DateTime now, string line)
        {
            Try(() =>
            {
                Directory.CreateDirectory(directory);
                var changedDay = currentPath == null || currentDate != now.Date;
                if (changedDay)
                {
                    SelectCurrentFile(now);
                }
                var record = FitRecord(line);
                var bytes = Utf8.GetByteCount(record);
                var length = File.Exists(currentPath) ? new FileInfo(currentPath).Length : 0;
                var rolled = length > 0 && length + bytes > maxFileBytes;
                if (rolled)
                {
                    var index = FileIndex(Path.GetFileName(currentPath)) + 1;
                    currentPath = Path.Combine(directory, prefix + now.ToString("yyyyMMdd") + "-" + index.ToString("D3") + ".log");
                }
                File.AppendAllText(currentPath, record, Utf8);
                totalBytes += bytes;
                if (changedDay || rolled || totalBytes > maxTotalBytes || now >= nextCleanup)
                {
                    Cleanup(now);
                }
            });
        }

        internal void Maintain(DateTime now)
        {
            if (now < nextCleanup)
            {
                return;
            }
            Try(() =>
            {
                Directory.CreateDirectory(directory);
                if (currentDate != now.Date)
                {
                    SelectCurrentFile(now);
                }
                Cleanup(now);
            });
        }

        private void SelectCurrentFile(DateTime now)
        {
            currentDate = now.Date;
            currentPath = OwnedFiles().Where(file => FileDate(file.Name) == now.Date)
                .OrderByDescending(file => FileIndex(file.Name)).Select(file => file.FullName).FirstOrDefault()
                ?? Path.Combine(directory, prefix + now.ToString("yyyyMMdd") + ".log");
        }

        private void Cleanup(DateTime now)
        {
            nextCleanup = now.AddMinutes(1);
            var files = OwnedFiles().OrderBy(file => FileDate(file.Name)).ThenBy(file => FileIndex(file.Name)).ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (FileDate(file.Name) <= now.Date.AddDays(-retentionDays) || total > maxTotalBytes)
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
            }
            totalBytes = total;
        }

        private FileInfo[] OwnedFiles()
        {
            return new DirectoryInfo(directory).GetFiles(prefix + "*.log")
                .Where(file => ownedName.IsMatch(file.Name) && FileDate(file.Name) != DateTime.MinValue
                    && (file.Attributes & FileAttributes.ReparsePoint) == 0).ToArray();
        }

        private DateTime FileDate(string name)
        {
            DateTime date;
            return DateTime.TryParseExact(ownedName.Match(name).Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date) ? date : DateTime.MinValue;
        }

        private int FileIndex(string name)
        {
            int index;
            return int.TryParse(ownedName.Match(name).Groups["index"].Value, out index) ? index : 0;
        }

        private string FitRecord(string line)
        {
            if (Utf8.GetByteCount(line) + Environment.NewLine.Length <= maxFileBytes)
            {
                return line + Environment.NewLine;
            }
            var suffix = " [recordTruncated originalLength=" + line.Length + "]" + Environment.NewLine;
            var budget = maxFileBytes - Utf8.GetByteCount(suffix);
            var low = 0;
            var high = line.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                if (Utf8.GetByteCount(line.Substring(0, middle)) <= budget)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            if (low > 0 && char.IsHighSurrogate(line[low - 1]))
            {
                low--;
            }
            return line.Substring(0, low) + suffix;
        }

        private void Try(Action action)
        {
            try
            {
                action();
                failureReported = false;
            }
            catch (Exception ex)
            {
                if (!failureReported)
                {
                    failureReported = true;
                    try
                    {
                        Console.Error.WriteLine("日志输出失败 [" + directory + "]: " + ex.Message);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }
}
