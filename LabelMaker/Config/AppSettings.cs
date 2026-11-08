using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LabelMaker.Config
{
    /// <summary>
    /// 应用配置：保存在 exe 同目录的 config.json，改完在托盘里选「设置」或重启程序生效。
    /// </summary>
    public class AppSettings
    {
        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private static AppSettings _current;

        /// <summary>当前生效配置（首次读取时从磁盘加载，没有文件则用默认值）</summary>
        public static AppSettings Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        // ===== 打标机（板卡 TCP 协议，本程序作为客户端主动连接） =====
        /// <summary>打标机 IP</summary>
        public string LaserIp { get; set; } = "127.0.0.1";
        /// <summary>打标机 TCP 端口</summary>
        public int LaserPort { get; set; } = 9000;
        /// <summary>登录密码，对应报文 {"F":"L","P":"密码"}</summary>
        public string LaserPassword { get; set; } = "123456";

        // ===== 模板 =====
        /// <summary>.orzx 模板在本机的绝对路径；HTTP 请求未指定模板时用这个</summary>
        public string TemplatePath { get; set; } = @"D:\template\label.orzx";

        // ===== HTTP 接口 =====
        /// <summary>HTTP 服务端口</summary>
        public int WebPort { get; set; } = 8723;
        /// <summary>是否允许局域网其它机器访问；false 时只监听 127.0.0.1</summary>
        public bool WebAllowLan { get; set; } = false;

        // ===== 其它 =====
        /// <summary>等待打标完成的超时时间（毫秒）</summary>
        public int MarkTimeoutMs { get; set; } = 30000;

        /// <summary>加载配置；文件不存在或读取失败时返回默认值</summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("读取配置失败：" + ex.Message);
            }
            return new AppSettings();
        }

        /// <summary>保存配置到 config.json</summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    // 中文不转义成 \uXXXX，方便直接手改 config.json
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(ConfigPath, json, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("保存配置失败：" + ex.Message);
            }
        }
    }
}
