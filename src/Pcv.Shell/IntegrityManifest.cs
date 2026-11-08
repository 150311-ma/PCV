using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Pcv.Shell
{
    /// <summary>
    /// 发布时算好的文件指纹清单（由 tools/build_integrity.py 生成）。
    ///
    /// 为什么要有这个：我们挡不住专业逆向，但能挡住「下载到一份被人改过的
    /// PCV」这种最常见的情况 —— 改过的文件和发布时算出的指纹对不上，
    /// 用户就会看到提醒，而不是无声地运行别人改过的代码。
    ///
    /// 清单放在程序旁边的 integrity.json 里，和其它文件一起分发。
    /// 它管的是「有没有被改过」，不是「能不能被读懂」—— 清单本身是明文的，
    /// 有心人可以连它一起改。这里守住的是大多数人，不是有心人。
    /// </summary>
    internal static class IntegrityManifest
    {
        /// <summary>清单文件名（放在程序同目录）。</summary>
        public const string FileName = "integrity.json";
        /// <summary>一条：相对路径 + 发布时的 sha256。</summary>
        public sealed class Entry
        {
            public string Path { get; set; }
            public string Sha256 { get; set; }
        }

        /// <summary>解析清单 JSON。格式：{"files":[{"path":"...","sha256":"..."}]}</summary>
        public static List<Entry> Parse(string json)
        {
            var result = new List<Entry>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                var document = Newtonsoft.Json.JsonConvert.DeserializeObject<ManifestDocument>(json);
                if (document?.Files == null) return result;
                foreach (var file in document.Files)
                {
                    if (file == null) continue;
                    if (string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Sha256)) continue;
                    result.Add(new Entry { Path = file.Path.Trim(), Sha256 = file.Sha256.Trim() });
                }
            }
            catch
            {
                // 清单坏了不等于被攻击：宁可少查这一项，也不要因为解析失败
                // 把正常用户挡在门外。
                return new List<Entry>();
            }
            return result;
        }

        /// <summary>
        /// 读程序同目录下的清单。读不到就返回空 —— 本地开发、或者用户
        /// 单独拷了 exe 出来跑，这两种情况下没有清单可比，跳过即可。
        /// </summary>
        public static List<Entry> LoadFromDisk(string baseDirectory)
        {
            try
            {
                var path = Path.Combine(baseDirectory, FileName);
                if (!File.Exists(path)) return new List<Entry>();
                return Parse(File.ReadAllText(path));
            }
            catch
            {
                return new List<Entry>();
            }
        }

        /// <summary>
        /// 清单是不是比某个文件新。
        ///
        /// 为什么要有这个判断：清单是打包时算的，正常情况下它比所有文件都新。
        /// 但本地重新编译过之后，程序集会比清单新 —— 这时候报「文件被改过」
        /// 是误报，会把正在调试的人自己拦下来。
        /// 所以比清单新的文件一律跳过：发布出去的东西不会出现这种情况。
        /// </summary>
        public static bool ManifestIsNewerThan(string baseDirectory, string filePath)
        {
            try
            {
                var manifest = Path.Combine(baseDirectory, FileName);
                if (!File.Exists(manifest)) return false;
                var manifestTime = File.GetLastWriteTimeUtc(manifest);
                var fileTime = File.GetLastWriteTimeUtc(filePath);
                return fileTime <= manifestTime;
            }
            catch
            {
                return false;
            }
        }

        private sealed class ManifestDocument
        {
            public List<Entry> Files { get; set; }
        }

        /// <summary>算一个文件的 sha256（小写十六进制）。</summary>
        public static string HashOf(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream);
                var builder = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
