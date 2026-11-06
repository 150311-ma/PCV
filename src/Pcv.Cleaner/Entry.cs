using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pcv.Modular.Abstractions;

namespace Pcv.Cleaner
{
    /// <summary>
    /// 清垃圾：只动两个地方——模块目录下以点开头的备份/回收站，和 logs 里放久了的日志。
    ///
    /// 删除这件事不能图快：这里每删一个目录都先算大小、再判断年龄、最后才动手，
    /// 而且全程只在自己算出来的候选清单里操作，绝不用通配符扫全盘。
    /// </summary>
    public sealed partial class Entry : IPclModule
    {
        private const int TrashKeepDays = 3;   // .trash 里的东西留 3 天，万一卸错了还能捞回来
        private const int BackupKeepDays = 7;  // 旧版本包留 7 天
        private const int LogKeepDays = 14;    // 日志留两周

        private IModuleContext _context;

        public void OnLoad(IModuleContext context) => _context = context;

        public void OnStart()
        {
            try
            {
                var result = CleanNow();
                var freed = result.Item1;
                var removed = result.Item2;

                if (removed.Count == 0)
                {
                    _context.Log.Info("没什么可清的");
                    return;
                }

                _context.Log.Info($"清掉 {removed.Count} 项，腾出 {freed / 1024.0 / 1024:0.##} MB");
                foreach (var path in removed)
                    _context.Log.Info("  删了 " + path);
            }
            catch (Exception ex)
            {
                _context.Log.Error("清理出错，已停手", ex);
            }
        }

        public void OnStop()
        {
        }

        /// <summary>立即清一次，返回（腾出的字节数, 删掉的路径清单）。</summary>
        internal Tuple<long, List<string>> CleanNow()
        {
            var removed = new List<string>();
            var freed = 0L;
            freed += CleanDotFolder(".trash", TrashKeepDays, removed);
            freed += CleanDotFolder(".backup", BackupKeepDays, removed);
            freed += CleanLogs(removed);
            return Tuple.Create(freed, removed);
        }

        /// <summary>只是看看有多少可清，不动手。</summary>
        internal Tuple<int, long> Survey()
        {
            var count = 0;
            var bytes = 0L;
            var deadline = DateTime.Now.AddDays(-Math.Min(TrashKeepDays, BackupKeepDays));

            foreach (var folderName in new[] { ".trash", ".backup" })
            {
                var folder = Path.Combine(_context.Host.ModulesDirectory, folderName);
                if (!Directory.Exists(folder)) continue;
                foreach (var directory in SafeDirectories(folder))
                {
                    var info = new DirectoryInfo(directory);
                    if (info.CreationTime > deadline) continue;
                    count++;
                    bytes += SizeOf(info);
                }
            }

            return Tuple.Create(count, bytes);
        }

        private long CleanDotFolder(string folderName, int keepDays, List<string> removed)
        {
            var folder = Path.Combine(_context.Host.ModulesDirectory, folderName);
            if (!Directory.Exists(folder)) return 0;

            var deadline = DateTime.Now.AddDays(-keepDays);
            var freed = 0L;

            foreach (var directory in SafeDirectories(folder))
            {
                var info = new DirectoryInfo(directory);
                if (info.CreationTime > deadline) continue;

                freed += SizeOf(info);
                TryDeleteDirectory(directory, removed);
            }

            return freed;
        }

        private long CleanLogs(List<string> removed)
        {
            var folder = Path.Combine(_context.Host.BaseDirectory, "logs");
            if (!Directory.Exists(folder)) return 0;

            // 自检报告也一起管；当前这次运行写的日志不碰
            var deadline = DateTime.Now.AddDays(-LogKeepDays);
            var freed = 0L;

            foreach (var file in SafeFiles(folder))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime > deadline) continue;
                if (string.Equals(info.Name, "modules.log", StringComparison.OrdinalIgnoreCase)) continue;

                freed += info.Length;
                try
                {
                    info.Delete();
                    removed.Add(file);
                }
                catch (Exception ex)
                {
                    _context.Log.Warn($"删不掉 {file}：{ex.Message}");
                }
            }

            return freed;
        }

        private void TryDeleteDirectory(string directory, List<string> removed)
        {
            try
            {
                Directory.Delete(directory, true);
                removed.Add(directory);
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"删不掉 {directory}：{ex.Message}");
            }
        }

        private static long SizeOf(DirectoryInfo info)
        {
            try
            {
                return info.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }

        private static IEnumerable<string> SafeDirectories(string folder)
        {
            try
            {
                return Directory.GetDirectories(folder);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeFiles(string folder)
        {
            try
            {
                return Directory.GetFiles(folder);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
