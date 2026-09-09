using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ControlDoor.FaceEvents
{
    public sealed class DeadLetterReplayResult
    {
        public int DeadLetterFilesFound { get; set; }

        public int Replayed { get; set; }

        public int Failed { get; set; }

        public List<string> Errors { get; } = new List<string>();
    }

    // 死信受控回放（复核 R2）：把死信目录的事件文件移回重试目录，由服务重启后的磁盘回放自动重投。
    // 仅移动文件、不解析内容；目标名使用新的随机 Guid，避免与死信目录的 "dl-" 配对命名产生歧义。
    // 设计为在服务停止时执行（--replay-dead-letters）；服务运行期间文件可能被占用导致个别移动失败，
    // 失败项保留在死信目录，可再次执行。
    public static class DeadLetterReplayTool
    {
        public static DeadLetterReplayResult ReplayToRetryDirectory(string retryDirectory)
        {
            var result = new DeadLetterReplayResult();
            if (string.IsNullOrWhiteSpace(retryDirectory))
            {
                result.Errors.Add("重试目录未配置。");
                return result;
            }

            var deadLetterDirectory = Path.Combine(retryDirectory, "dead-letter");
            if (!Directory.Exists(deadLetterDirectory))
            {
                return result;
            }

            Directory.CreateDirectory(retryDirectory);
            var files = Directory.EnumerateFiles(deadLetterDirectory, "*.json").ToList();
            result.DeadLetterFilesFound = files.Count;
            foreach (var file in files)
            {
                try
                {
                    var target = Path.Combine(retryDirectory, Guid.NewGuid().ToString("N") + ".json");
                    File.Move(file, target);
                    result.Replayed++;
                }
                catch (IOException ex)
                {
                    result.Failed++;
                    result.Errors.Add(file + ": " + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    result.Failed++;
                    result.Errors.Add(file + ": " + ex.Message);
                }
            }

            return result;
        }
    }
}
