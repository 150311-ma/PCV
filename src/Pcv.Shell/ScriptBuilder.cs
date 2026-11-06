using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Packages;

namespace Pcv.Shell
{
    /// <summary>
    /// 跑"构建脚本"：DeepSeek 给的那种 .py，自己写清单、编 dll、打包。
    ///
    /// 两件事值得说明：
    ///   1. 这类脚本里通常硬编码了 `PCV_DIR = r"C:\PCV"`。拖进来时我们会把这一行改成真实安装目录，
    ///      并且**把改了什么打进日志**——悄悄改用户的脚本不体面，改完不吭声更糟。
    ///   2. 跑完不看它嘴上说什么，看磁盘上多出了哪个 .pclpkg。输出里的路径只是线索。
    /// </summary>
    public sealed class ScriptBuilder
    {
        private readonly string _appDirectory;
        private readonly ILogger _log;

        public ScriptBuilder(string appDirectory, ILogger log)
        {
            _appDirectory = appDirectory;
            _log = log;
        }

        public sealed class Result
        {
            public bool Success { get; set; }
            public string PackagePath { get; set; }
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string Message { get; set; }
            public string WorkDirectory { get; set; }
        }

        /// <summary>把脚本适配好、跑起来。onLine 用来实时把输出推给界面。</summary>
        public Result Run(string scriptPath, Action<string> onLine)
        {
            var id = DateTime.Now.ToString("MMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
            var work = Path.Combine(_appDirectory, "PCLData", "build", id);
            Directory.CreateDirectory(work);

            var fileName = Path.GetFileName(scriptPath);
            var adapted = Path.Combine(work, fileName);
            var notes = Adapt(scriptPath, adapted);

            var output = new StringBuilder();
            void Emit(string line)
            {
                if (line == null) return;
                output.AppendLine(line);
                onLine?.Invoke(line);
            }

            Emit("[工作目录] " + work);
            foreach (var note in notes) Emit("[适配] " + note);

            // 给脚本两个参数，让它不用猜路径：
            //   [1] 工作目录（产物写这里，用相对路径就是它）
            //   [2] 契约程序集 Pcv.Modular.Abstractions.dll 的绝对路径
            // 这样提示词里就可以严禁写死任何绝对路径。
            var contract = Path.Combine(_appDirectory, "Pcv.Modular.Abstractions.dll");
            var info = new ProcessStartInfo
            {
                FileName = PythonPath(),
                Arguments = "-u \"" + adapted + "\" \"" + work + "\" \"" + contract + "\"",
                WorkingDirectory = work,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var exitCode = -1;
            try
            {
                using (var process = new Process { StartInfo = info })
                {
                    process.OutputDataReceived += (s, e) => Emit(e.Data);
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) Emit("[错误] " + e.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit(300000);
                    if (!process.HasExited) process.Kill();
                    exitCode = process.HasExited ? process.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                return new Result
                {
                    Success = false,
                    ExitCode = -1,
                    Output = output.ToString(),
                    WorkDirectory = work,
                    Message = "跑不起来：" + ex.Message
                };
            }

            var package = FindPackage(output.ToString(), work);
            var result = new Result
            {
                ExitCode = exitCode,
                Output = output.ToString(),
                WorkDirectory = work,
                PackagePath = package
            };

            if (exitCode == 0 && package != null)
            {
                result.Success = true;
                result.Message = "构建成功：" + Path.GetFileName(package);
            }
            else if (exitCode == 0)
            {
                result.Message = "脚本跑完了，但没找到 .pclpkg。输出里的路径可能不对。";
            }
            else
            {
                result.Message = $"脚本以退出码 {exitCode} 结束，见上面的输出。";
            }

            _log.Info($"[build] {fileName}：{result.Message}");
            return result;
        }

        /// <summary>
        /// 适配：把脚本里的 PCV_DIR 指到真实安装目录。
        /// 只动这一处，其它行原样保留——改多了就成了"重写用户的脚本"。
        /// </summary>
        private List<string> Adapt(string source, string destination)
        {
            var notes = new List<string>();
            var text = File.ReadAllText(source, Encoding.UTF8);

            var pattern = new Regex(@"^(\s*PCV_DIR\s*=\s*r?)([""']).*?\2", RegexOptions.Multiline);
            var matches = pattern.Matches(text);
            if (matches.Count == 0)
            {
                File.WriteAllText(destination, text, Encoding.UTF8);
                notes.Add("脚本里没有 PCV_DIR，原样运行");
                return notes;
            }

            // 结尾不能留反斜杠：r"C://dir//" 在 Python 里是"字符串没结束"
            var directory = _appDirectory.TrimEnd('\\', '/');
            var replaced = pattern.Replace(text, m => m.Groups[1].Value + "\"" + directory + "\"");
            File.WriteAllText(destination, replaced, Encoding.UTF8);
            notes.Add($"把 PCV_DIR 改成 {directory}（改前是 {matches[0].Value.Split('=')[1].Trim()}）");
            return notes;
        }

        /// <summary>找产物：先看输出里报的路径，再看工作目录里新出现的 .pclpkg。</summary>
        private static string FindPackage(string output, string workDirectory)
        {
            foreach (var line in output.Split('\n').Reverse())
            {
                var trimmed = line.Trim();
                if (!trimmed.EndsWith(".pclpkg", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(trimmed)) return trimmed;
            }

            try
            {
                var candidate = Directory.GetFiles(workDirectory, "*.pclpkg", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (candidate != null) return candidate;
            }
            catch
            {
                // 目录读不了就算了
            }

            return null;
        }

        private static string PythonPath()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var folder in path.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), "python.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // 非法路径跳过
                }
            }
            return "python.exe";
        }
    }
}
