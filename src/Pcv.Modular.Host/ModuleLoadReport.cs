using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Pcv.Modular.Abstractions;

namespace Pcv.Modular.Host
{
    /// <summary>加载结果快照。既给人看（Render），也给自动化校验用（IsHealthy / ExitCode）。</summary>
    public sealed class ModuleLoadReport
    {
        private readonly List<ModuleEntry> _entries;

        internal ModuleLoadReport(IEnumerable<ModuleEntry> entries, string modulesDirectory, HostInfo host)
        {
            _entries = entries.ToList();
            ModulesDirectory = modulesDirectory;
            Host = host;
        }

        public IReadOnlyList<ModuleEntry> Entries => _entries;
        public string ModulesDirectory { get; }
        public HostInfo Host { get; }

        public int StartedCount => _entries.Count(e => e.State == ModuleState.Started);
        public int FailedCount => _entries.Count(e => e.State == ModuleState.Failed);
        public int SkippedCount => _entries.Count(e => e.State == ModuleState.Skipped);

        /// <summary>健康 = 没有被判失败的模块，且没有被跳过的模块。</summary>
        public bool IsHealthy => FailedCount == 0 && SkippedCount == 0 && _entries.Count > 0;

        /// <summary>0 = 全部正常；1 = 有失败/跳过；2 = 一个模块都没发现。</summary>
        public int ExitCode => _entries.Count == 0 ? 2 : (IsHealthy ? 0 : 1);

        public string Render()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"宿主：{Host.Name} {Host.Version}");
            builder.AppendLine($"模块目录：{ModulesDirectory}");
            builder.AppendLine();

            var idWidth = Math.Max(8, _entries.Select(e => e.Id.Length).DefaultIfEmpty(8).Max());
            var nameWidth = Math.Max(10, _entries.Select(e => e.Manifest?.DisplayName?.Length ?? 0).DefaultIfEmpty(10).Max());
            var line = $"{"模块".PadRight(nameWidth)}  {"Id".PadRight(idWidth)}  {"版本",-8}  {"状态",-10}  {"加载",6}  {"启动",6}  说明";
            builder.AppendLine(line);
            builder.AppendLine(new string('-', line.Length + 8));

            foreach (var entry in _entries)
            {
                builder.AppendLine(
                    $"{(entry.Manifest?.DisplayName ?? "?").PadRight(nameWidth)}  " +
                    $"{entry.Id.PadRight(idWidth)}  " +
                    $"{(entry.Manifest?.Version ?? "-"),-8}  " +
                    $"{StateText(entry.State),-10}  " +
                    $"{entry.LoadMilliseconds + "ms",6}  " +
                    $"{entry.StartMilliseconds + "ms",6}  " +
                    $"{entry.Error ?? ""}");
            }

            builder.AppendLine();
            builder.AppendLine($"已启动 {StartedCount}　失败 {FailedCount}　跳过 {SkippedCount}　共 {_entries.Count}");
            builder.AppendLine(IsHealthy ? "结论：模块图健康。" : "结论：存在异常模块，见上表。");
            return builder.ToString();
        }

        private static string StateText(ModuleState state)
        {
            switch (state)
            {
                case ModuleState.Discovered: return "已发现";
                case ModuleState.Resolved: return "已解析";
                case ModuleState.Loaded: return "已加载";
                case ModuleState.Started: return "运行中";
                case ModuleState.Stopped: return "已停止";
                case ModuleState.Skipped: return "已跳过";
                case ModuleState.Failed: return "失败";
                default: return state.ToString();
            }
        }

        public override string ToString() => Render();
    }
}
