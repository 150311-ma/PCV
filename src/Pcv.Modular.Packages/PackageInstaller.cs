using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;

namespace Pcv.Modular.Packages
{
    /// <summary>
    /// 一个功能框架包（.pclpkg / .zip）的检查结果。Inspect 只读，不碰磁盘上的模块目录。
    /// </summary>
    public sealed class PackageInspection
    {
        public string PackagePath { get; set; }
        public ModuleManifest Manifest { get; set; }
        public string Sha256 { get; set; }
        public long TotalBytes { get; set; }
        public int EntryCount { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Files { get; } = new List<string>();

        public bool IsValid => Errors.Count == 0 && Manifest != null;

        public string Summary()
        {
            if (!IsValid) return "无法安装：" + string.Join("；", Errors);
            return $"{Manifest.DisplayName} {Manifest.Version}（{EntryCount} 个文件，{TotalBytes / 1024.0:0.#} KB）";
        }
    }

    public sealed class InstallResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string ModuleId { get; set; }
        public string InstalledPath { get; set; }
        public string BackupPath { get; set; }
        public PackageInspection Inspection { get; set; }

        public static InstallResult Fail(string message, PackageInspection inspection = null)
            => new InstallResult { Success = false, Message = message, Inspection = inspection };
    }

    public sealed class InstalledModule
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Directory { get; set; }
        public DateTime InstalledAt { get; set; }
        public string SourceFile { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// 功能框架包安装器。
    ///
    /// 设计原则（这些是"成熟体系"和"能跑就行"的区别）：
    ///   1. 全部先在 staging 目录里做完，最后一步才原子移动 —— 半途失败不会留下半个模块；
    ///   2. 覆盖旧版本前先备份，提交失败自动回滚；
    ///   3. 拒绝 zip slip（../ 与绝对路径）、限制条目数与解压体积；
    ///   4. 清单先校验（id 格式、程序集存在、依赖已装、宿主版本）；
    ///   5. 每个包装完留 install.json（来源、sha256、时间），卸载时不靠猜。
    /// </summary>
    public sealed class PackageInstaller
    {
        private const int MaxEntries = 2000;
        private const long MaxUncompressedBytes = 200L * 1024 * 1024;

        private readonly string _modulesDirectory;
        private readonly ILogger _log;

        public PackageInstaller(string modulesDirectory, ILogger log)
        {
            _modulesDirectory = Path.GetFullPath(modulesDirectory);
            _log = log;
        }

        public string ModulesDirectory => _modulesDirectory;

        // ── 检查 ────────────────────────────────────────────────────────────

        /// <summary>只读检查：能不能装、装的是谁、有哪些隐患。不改动任何东西。</summary>
        public PackageInspection Inspect(string packagePath)
        {
            var inspection = new PackageInspection { PackagePath = packagePath };
            if (!File.Exists(packagePath))
            {
                inspection.Errors.Add("文件不存在");
                return inspection;
            }

            try
            {
                inspection.Sha256 = ComputeSha256(packagePath);
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    inspection.EntryCount = archive.Entries.Count;
                    if (inspection.EntryCount == 0) inspection.Errors.Add("空压缩包");
                    if (inspection.EntryCount > MaxEntries) inspection.Errors.Add($"条目过多（{inspection.EntryCount} > {MaxEntries}）");

                    ZipArchiveEntry manifestEntry = null;
                    string manifestPrefix = "";
                    foreach (var entry in archive.Entries)
                    {
                        var name = entry.FullName.Replace('\\', '/');
                        if (IsUnsafePath(name))
                        {
                            inspection.Errors.Add("包含非法路径：" + entry.FullName);
                            continue;
                        }
                        inspection.TotalBytes += entry.Length;
                        if (entry.Length > 0) inspection.Files.Add(name);

                        if (string.Equals(name, "module.json", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestEntry = entry;
                            manifestPrefix = "";
                        }
                        else if (name.EndsWith("/module.json", StringComparison.OrdinalIgnoreCase) && manifestEntry == null)
                        {
                            // 允许套一层目录（很多人压缩时会多一层文件夹）
                            manifestEntry = entry;
                            manifestPrefix = name.Substring(0, name.Length - "module.json".Length);
                        }
                    }

                    if (inspection.TotalBytes > MaxUncompressedBytes)
                        inspection.Errors.Add($"解压后过大（{inspection.TotalBytes / 1024 / 1024} MB > 200 MB）");

                    if (manifestEntry == null)
                    {
                        inspection.Errors.Add("压缩包里没有 module.json");
                        return inspection;
                    }

                    string manifestText;
                    using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                        manifestText = reader.ReadToEnd();

                    ModuleManifest manifest;
                    try
                    {
                        manifest = JsonConvert.DeserializeObject<ModuleManifest>(manifestText);
                    }
                    catch (Exception ex)
                    {
                        inspection.Errors.Add("module.json 不是合法 JSON：" + ex.Message);
                        return inspection;
                    }

                    if (manifest == null)
                    {
                        inspection.Errors.Add("module.json 为空");
                        return inspection;
                    }

                    inspection.Manifest = manifest;
                    foreach (var error in manifest.Validate()) inspection.Errors.Add(error);

                    if (!IsValidModuleId(manifest.Id))
                        inspection.Errors.Add($"模块 Id 不合规（要求小写点分，如 pcl.demo）：{manifest.Id}");

                    // 入口文件必须在包内：程序集型看 assembly，进程型看 script
                    var entryFile = string.Equals(manifest.Kind, "process", StringComparison.OrdinalIgnoreCase)
                        ? manifest.Script
                        : manifest.Assembly;

                    if (!string.IsNullOrWhiteSpace(entryFile))
                    {
                        var expected = (manifestPrefix + entryFile.Replace('\\', '/')).ToLowerInvariant();
                        if (!inspection.Files.Any(f => f.ToLowerInvariant() == expected))
                            inspection.Errors.Add($"清单声明了 {entryFile}，但压缩包内没有这个文件");
                    }

                    // 依赖必须已经装好
                    foreach (var dependency in manifest.Dependencies ?? new List<string>())
                    {
                        if (!Directory.Exists(Path.Combine(_modulesDirectory, dependency)))
                            inspection.Errors.Add($"缺少前置框架：{dependency}（先安装它）");
                    }

                    if (manifest.LoadPriority < 0)
                        inspection.Warnings.Add("loadPriority 为负，会排在基础模块之前，通常不需要这样");
                }
            }
            catch (InvalidDataException)
            {
                inspection.Errors.Add("不是有效的 zip / pclpkg 文件");
            }
            catch (Exception ex)
            {
                inspection.Errors.Add("检查失败：" + ex.Message);
            }

            return inspection;
        }

        // ── 安装 ────────────────────────────────────────────────────────────

        public InstallResult Install(string packagePath, bool overwrite = false)
        {
            var inspection = Inspect(packagePath);
            if (!inspection.IsValid) return InstallResult.Fail(inspection.Summary(), inspection);

            var moduleId = inspection.Manifest.Id;
            var target = Path.Combine(_modulesDirectory, moduleId);
            var stagingRoot = Path.Combine(_modulesDirectory, ".staging");
            var backupRoot = Path.Combine(_modulesDirectory, ".backup");
            var staging = Path.Combine(stagingRoot, moduleId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string backup = null;

            try
            {
                Directory.CreateDirectory(stagingRoot);
                Directory.CreateDirectory(_modulesDirectory);

                ExtractSafely(packagePath, staging);

                // 确认解压出来的 module.json 就是刚才检查的那份，防止检查与解压不一致
                var extractedManifest = Path.Combine(staging, "module.json");
                if (!File.Exists(extractedManifest))
                {
                    var nested = Directory.GetDirectories(staging);
                    if (nested.Length == 1 && File.Exists(Path.Combine(nested[0], "module.json")))
                    {
                        // 拍平一层目录
                        foreach (var item in Directory.GetFileSystemEntries(nested[0]))
                        {
                            var destination = Path.Combine(staging, Path.GetFileName(item));
                            if (Directory.Exists(item)) Directory.Move(item, destination);
                            else File.Move(item, destination);
                        }
                        Directory.Delete(nested[0]);
                    }
                }

                if (!File.Exists(Path.Combine(staging, "module.json")))
                    throw new InvalidOperationException("解压后找不到 module.json");

                // 写安装记录
                var record = new InstalledModule
                {
                    Id = moduleId,
                    Name = inspection.Manifest.DisplayName,
                    Version = inspection.Manifest.Version,
                    Directory = target,
                    InstalledAt = DateTime.Now,
                    SourceFile = Path.GetFileName(packagePath),
                    Sha256 = inspection.Sha256
                };
                File.WriteAllText(Path.Combine(staging, "install.json"),
                    JsonConvert.SerializeObject(record, Formatting.Indented), Encoding.UTF8);

                // 已存在同 Id：不覆盖就直接拒绝，要覆盖先挪到 backup
                if (Directory.Exists(target))
                {
                    if (!overwrite)
                    {
                        var existingVersion = ReadInstalledVersion(target);
                        return InstallResult.Fail(
                            $"已安装 {moduleId}{(existingVersion == null ? "" : " " + existingVersion)}。确认覆盖请勾选\"允许覆盖\"。", inspection);
                    }

                    Directory.CreateDirectory(backupRoot);
                    backup = Path.Combine(backupRoot, moduleId + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                    Directory.Move(target, backup);
                }

                // 提交：同一卷上的目录移动是原子的
                Directory.Move(staging, target);
                _log.Info($"已安装框架 {moduleId} {inspection.Manifest.Version}（来源 {Path.GetFileName(packagePath)}）");

                return new InstallResult
                {
                    Success = true,
                    Message = $"已安装 {inspection.Manifest.DisplayName} {inspection.Manifest.Version}",
                    ModuleId = moduleId,
                    InstalledPath = target,
                    BackupPath = backup,
                    Inspection = inspection
                };
            }
            catch (Exception ex)
            {
                _log.Error($"安装 {moduleId} 失败", ex);
                TryDelete(staging);

                // 回滚：把备份挪回原位，绝不留下"既没新版也没旧版"的状态
                if (backup != null && Directory.Exists(backup) && !Directory.Exists(target))
                {
                    try
                    {
                        Directory.Move(backup, target);
                        _log.Warn($"已回滚到安装前的版本：{moduleId}");
                    }
                    catch (Exception rollbackError)
                    {
                        _log.Error($"回滚失败，备份仍在 {backup}，请手动恢复", rollbackError);
                    }
                }
                return InstallResult.Fail("安装失败：" + ex.Message, inspection);
            }
            finally
            {
                TryDelete(stagingRoot);
            }
        }

        // ── 卸载 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 卸载。已加载的模块程序集在 .NET Framework 里卸不掉，所以这里只把它挪进 .trash，
        /// 下次启动时不会被发现，等于"重启后彻底消失"。不骗人说已经删干净了。
        /// </summary>
        public bool Uninstall(string moduleId, out string message)
        {
            message = null;
            if (!IsValidModuleId(moduleId))
            {
                message = "模块 Id 不合规";
                return false;
            }

            var target = Path.Combine(_modulesDirectory, moduleId);
            if (!Directory.Exists(target))
            {
                message = "没有安装这个框架";
                return false;
            }

            // 基础模块不允许卸，否则壳自己就废了
            if (string.Equals(moduleId, "pcv.core", StringComparison.OrdinalIgnoreCase))
            {
                message = "pcv.core 是基础框架，不能卸载";
                return false;
            }

            try
            {
                var trash = Path.Combine(_modulesDirectory, ".trash");
                Directory.CreateDirectory(trash);
                var destination = Path.Combine(trash, moduleId + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                Directory.Move(target, destination);
                message = "已移出模块目录，重启后彻底生效";
                _log.Info($"已卸载框架 {moduleId}（移到 {destination}）");
                return true;
            }
            catch (Exception ex)
            {
                message = "卸载失败：" + ex.Message;
                _log.Error($"卸载 {moduleId} 失败", ex);
                return false;
            }
        }

        // ── 列表 ────────────────────────────────────────────────────────────

        public IReadOnlyList<InstalledModule> ListInstalled()
        {
            var result = new List<InstalledModule>();
            if (!Directory.Exists(_modulesDirectory)) return result;

            foreach (var directory in Directory.GetDirectories(_modulesDirectory))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;

                var manifestPath = Path.Combine(directory, "module.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var manifest = JsonConvert.DeserializeObject<ModuleManifest>(File.ReadAllText(manifestPath));
                    var record = ReadRecord(directory);
                    result.Add(new InstalledModule
                    {
                        Id = manifest?.Id ?? name,
                        Name = manifest?.DisplayName ?? name,
                        Version = manifest?.Version ?? "?",
                        Directory = directory,
                        InstalledAt = record?.InstalledAt ?? Directory.GetCreationTime(directory),
                        SourceFile = record?.SourceFile,
                        Sha256 = record?.Sha256
                    });
                }
                catch
                {
                    // 坏掉的清单不影响其它模块的展示
                    result.Add(new InstalledModule { Id = name, Name = name + "（清单损坏）", Version = "?", Directory = directory });
                }
            }

            return result.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ── 内部工具 ────────────────────────────────────────────────────────

        private static void ExtractSafely(string packagePath, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);
            var targetRoot = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项

                    var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("压缩包包含越界路径：" + entry.FullName);

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination, true);
                }
            }
        }

        private static bool IsUnsafePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.StartsWith("/") || name.StartsWith("\\")) return true;
            if (name.Length > 1 && name[1] == ':') return true;
            var parts = name.Split('/');
            return parts.Any(p => p == "..");
        }

        private static bool IsValidModuleId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (id.Length > 64) return false;
            if (!id.Contains(".")) return false;
            return id.All(c => char.IsLower(c) || char.IsDigit(c) || c == '.' || c == '-');
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            }
        }

        private static InstalledModule ReadRecord(string directory)
        {
            try
            {
                var path = Path.Combine(directory, "install.json");
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<InstalledModule>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static string ReadInstalledVersion(string directory)
        {
            try
            {
                var path = Path.Combine(directory, "module.json");
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<ModuleManifest>(File.ReadAllText(path))?.Version;
            }
            catch
            {
                return null;
            }
        }

        private static void TryDelete(string directory)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch
            {
                // 删不掉就算了，下次安装会覆盖
            }
        }
    }
}
