using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pcv.Modular.Contracts;

namespace Pcv.Core
{
    /// <summary>
    /// JSON 键值配置。
    ///
    /// 只做持久化，不做"选项声明"——默认值由使用方给，
    /// 配置层不需要知道界面长什么样。写盘是原子的（先写 .tmp 再换）。
    /// </summary>
    public sealed class JsonSettings : ISettingsService
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private JObject _data = new JObject();
        private bool _dirty;

        public JsonSettings(string path)
        {
            _path = path;
            Load();
        }

        public string LastError { get; private set; }

        public IReadOnlyCollection<string> Keys
        {
            get
            {
                lock (_gate) return _data.Properties().Select(p => p.Name).ToList();
            }
        }

        public bool Load()
        {
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_path))
                    {
                        _data = new JObject();
                        return true;
                    }

                    var text = File.ReadAllText(_path);
                    _data = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                    _dirty = false;
                    return true;
                }
                catch (Exception ex)
                {
                    // 配置文件坏了不能让程序起不来：从空配置开始，把原因记下来
                    _data = new JObject();
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public T GetValue<T>(string key, T defaultValue = default)
        {
            if (string.IsNullOrEmpty(key)) return defaultValue;
            lock (_gate)
            {
                var token = _data[key];
                if (token == null || token.Type == JTokenType.Null) return defaultValue;
                try
                {
                    return token.ToObject<T>();
                }
                catch (Exception ex)
                {
                    LastError = "读取 " + key + " 失败：" + ex.Message;
                    return defaultValue;
                }
            }
        }

        public void SetValue<T>(string key, T value)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                _data[key] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                _dirty = true;
            }
        }

        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate)
            {
                var removed = _data.Remove(key);
                if (removed) _dirty = true;
                return removed;
            }
        }

        public bool Save()
        {
            lock (_gate)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    // 先写临时文件再替换：写一半断电也不至于把配置写废
                    var temporary = _path + ".tmp";
                    File.WriteAllText(temporary, _data.ToString(Formatting.Indented));
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(temporary, _path);

                    _dirty = false;
                    LastError = null;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public bool HasUnsavedChanges
        {
            get
            {
                lock (_gate) return _dirty;
            }
        }
    }
}
