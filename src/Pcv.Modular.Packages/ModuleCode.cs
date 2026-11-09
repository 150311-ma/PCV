using System;
using System.Security.Cryptography;
using System.Text;

namespace Pcv.Modular.Packages
{
    /// <summary>
    /// 模块数字：一个模块的「身份证号」。
    ///
    /// 为什么需要它：模块散落在各人的 GitHub 仓库里，没有中心服务器分配编号。
    /// 那就让编号从内容本身算出来 —— 同样的模块、同样的版本、同样的字节，
    /// 在任何机器上算出来都是同一个号；改了一个字节，号就变了。
    ///
    /// 组成：
    ///   模块 id + 版本 + 包内容的 sha256  →  sha256  →  取前 60 位  →  Crockford Base32
    /// 得到 12 个字符，写成 PCV-XXXX-XXXX-XXXX。
    ///
    /// 为什么用 Crockford Base32：它去掉了 I / L / O / U 四个容易和 1 / 0 混的字母，
    /// 用户手抄、打电话报号、贴在简介里都不会认错。这是给人用的号，不是给机器用的。
    ///
    /// 校验：tools/check_module_code.py 有金标准向量，两种语言的实现必须算出同一个号。
    /// 改了算法就必须同时改两处和那边向量，否则闸门会红。
    /// </summary>
    public static class ModuleCode
    {
        /// <summary>Crockford Base32：去掉 I L O U，避免 1/0 混淆。</summary>
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>算法版本。将来真要改算法，用它区分新旧号。</summary>
        public const int SpecVersion = 1;

        /// <summary>按「id + 版本 + 包 sha256」算模块数字。</summary>
        /// <param name="moduleId">模块清单里的 id，例如 pcv.stat</param>
        /// <param name="version">模块清单里的 version，例如 3.1.1</param>
        /// <param name="packageSha256">整包内容的 sha256（小写十六进制）</param>
        public static string Compute(string moduleId, string version, string packageSha256)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                throw new ArgumentException("模块 id 不能为空", nameof(moduleId));
            if (string.IsNullOrWhiteSpace(packageSha256))
                throw new ArgumentException("包的 sha256 不能为空", nameof(packageSha256));

            var stableId = (moduleId ?? "").Trim().ToLowerInvariant();
            var stableVersion = (version ?? "").Trim();
            var stableHash = (packageSha256 ?? "").Trim().ToLowerInvariant();

            // 三段用换行拼：id 里可能有连字符和点，用分隔符才不会串号
            var payload = stableId + "\n" + stableVersion + "\n" + stableHash;
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));

            // 12 个 5 位字符 = 60 位，取前 7 个字节（56 位）+ 后 4 位
            var bits = 0UL;
            for (var i = 0; i < 8; i++) bits = (bits << 8) | digest[i];
            // 只留最高 60 位
            bits >>= 4;

            var chars = new char[12];
            for (var i = 11; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(bits & 0x1F)];
                bits >>= 5;
            }

            return "PCV-" + new string(chars, 0, 4) + "-"
                         + new string(chars, 4, 4) + "-"
                         + new string(chars, 8, 4);
        }

        /// <summary>
        /// 把手抄来的号规整成标准写法：先剥掉 PCV 前缀，再忽略大小写与连字符，
        /// 顺便把 Crockford 允许的误读（O→0、I/L→1）纠正回来。返回 null 表示这个号根本不成立。
        /// </summary>
        public static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var text = input.Trim().ToUpperInvariant();
            // 前缀要先剥掉：P/C/V 里的 P 和 V 都不在字母表里，不剥会整条判非法
            if (text.StartsWith("PCV", StringComparison.Ordinal)) text = text.Substring(3);

            var builder = new StringBuilder(12);
            foreach (var raw in text)
            {
                if (raw == '-' || raw == ' ') continue;
                var ch = raw;
                // Crockford 的宽容规则：这几种手抄时最容易混
                if (ch == 'O') ch = '0';
                else if (ch == 'I' || ch == 'L') ch = '1';
                else if (ch == 'U') continue; // U 不在字母表里，通常是误输入

                if (Alphabet.IndexOf(ch) < 0) return null;
                builder.Append(ch);
                if (builder.Length > 12) return null;
            }

            if (builder.Length != 12) return null;
            var body = builder.ToString();
            return "PCV-" + body.Substring(0, 4) + "-" + body.Substring(4, 4) + "-" + body.Substring(8, 4);
        }

        /// <summary>两个号是不是同一个（容忍大小写、连字符、手抄误读）。</summary>
        public static bool Same(string left, string right)
        {
            var a = Normalize(left);
            var b = Normalize(right);
            return a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);
        }
    }
}
