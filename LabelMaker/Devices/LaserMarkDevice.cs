using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;

namespace LabelMaker.Devices
{
    /// <summary>
    /// SPI 图元替换项：模板中序号 N 的文本图元替换为 T
    /// </summary>
    public class PlotTextItem
    {
        public int N { get; set; }
        public string T { get; set; }
    }

    /// <summary>
    /// 激光打标机 TCP 客户端封装（基于 TouchSocket 4.3.x 异步 API）。
    /// 板卡 JSON 协议，报文以 '#' 结束：
    ///   登录  {"F":"L","P":"密码"}#        -> 返回 S==1 表示成功
    ///   开档  {"F":"ODC","DP":"路径"}#     -> 打开 orzx 模板，应答里的 DC 是设备认的文档名
    ///   替换  {"F":"SPI","DC":"文档名","P":[{N,T}]}# -> 应答 S==0 表示失败
    ///   打标  {"F":"M"}#                   -> 触发标刻
    ///   完成  设备主动推送 {"F":"MS"...}#   -> 物理打标结束
    /// 日志约定：[TCP→] 为发送、[TCP←] 为接收，便于和现场抓包对照。
    /// </summary>
    public class LaserMarkDevice : IDisposable
    {
        private const string EndFlag = "#"; // 报文结束符（由 TerminatorPackageAdapter 自动追加）
        /// <summary>单条指令等待应答的超时。超时只记日志、不判失败，兼容部分板卡对 ODC/SPI 不回包</summary>
        private const int AckTimeoutMs = 2000;

        /// <summary>收发报文使用的文本编码</summary>
        private readonly Encoding _encoding = new UTF8Encoding(false);

        /// <summary>JSON 序列化：中文不转义成 \uXXXX，保持原字符</summary>
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private TcpClient _client;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TaskCompletionSource<bool> _loginTcs;
        private TaskCompletionSource<bool> _markTcs;
        private TaskCompletionSource<JsonElement> _odcTcs;
        private TaskCompletionSource<JsonElement> _spiTcs;
        /// <summary>本台设备是否会对 ODC / SPI 回包。首次等待超时后自动置 false，后续指令不再空等</summary>
        private bool _odcAckEnabled = true;
        private bool _spiAckEnabled = true;

        /// <summary>是否已建立 TCP 连接</summary>
        public bool IsConnected => _client != null && _client.Online;

        /// <summary>是否已登录打标机</summary>
        public bool IsLoggedIn { get; private set; }

        /// <summary>日志输出事件</summary>
        public event Action<string> OnLog;

        /// <summary>打标完成事件（success=是否成功, msg=设备原始返回报文）</summary>
        public event Action<bool, string> OnMarkFinished;

        /// <summary>
        /// 连接并登录打标机
        /// </summary>
        public async Task<bool> ConnectAsync(string ip, int port, string password, int timeoutMs = 8000)
        {
            try
            {
                if (IsConnected && IsLoggedIn) return true;

                if (_client == null)
                {
                    var config = new TouchSocketConfig();
                    config.SetRemoteIPHost(new IPHost(ip + ":" + port));
                    // 关键：按 '#' 分隔数据包，解决 TCP 粘包/半包；
                    // 注意它发送时也会自动追加结束符，所以报文里不能再自己加 '#'，否则线上会变成 "##"
                    config.SetSingleStreamDataHandlingAdapter(() => new TerminatorPackageAdapter(EndFlag));

                    _client = await config.BuildClientAsync<TcpClient>(_cts.Token);

                    _client.Connected = (c, e) =>
                    {
                        Log("TCP 已连接：" + ip + ":" + port);
                        return Task.CompletedTask;
                    };
                    _client.Closed = (c, e) =>
                    {
                        IsLoggedIn = false;
                        Log("连接已关闭");
                        _loginTcs?.TrySetResult(false);
                        _markTcs?.TrySetResult(false);
                        return Task.CompletedTask;
                    };
                    _client.Received = (c, e) =>
                    {
                        OnReceived(e);
                        return Task.CompletedTask;
                    };
                }

                if (!_client.Online)
                    await _client.ConnectAsync(_cts.Token);

                // 登录
                _loginTcs = new TaskCompletionSource<bool>();
                var loginCmd = JsonSerializer.Serialize(new Dictionary<string, object> { ["F"] = "L", ["P"] = password }, JsonOpts);
                Log("[TCP→] L 登录：" + loginCmd + EndFlag);
                await _client.SendAsync(new ReadOnlyMemory<byte>(_encoding.GetBytes(loginCmd)), _cts.Token);

                var okTask = await Task.WhenAny(_loginTcs.Task, Task.Delay(timeoutMs));
                IsLoggedIn = okTask == _loginTcs.Task && _loginTcs.Task.Result;
                if (IsLoggedIn)
                {
                    // 换设备/重新连接时重新探测该设备是否会回 ODC、SPI 应答
                    _odcAckEnabled = true;
                    _spiAckEnabled = true;
                }
                Log(IsLoggedIn ? "打标机登录成功" : "打标机登录失败/超时");
                if (IsLoggedIn) Log("文本编码：" + _encoding.WebName);
                return IsLoggedIn;
            }
            catch (Exception ex)
            {
                Log("连接打标机异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 执行一次完整打标：打开模板 -> SPI 替换图元 -> 启动标刻 -> 等待 MS 完成
        /// </summary>
        /// <param name="items">图元替换项列表</param>
        /// <param name="templatePath">orzx 模板绝对路径</param>
        /// <param name="timeoutMs">等待打标完成的超时时间</param>
        public async Task<bool> MarkAsync(List<PlotTextItem> items, string templatePath, int timeoutMs = 30000)
        {
            if (!IsConnected || !IsLoggedIn)
            {
                Log("未连接/未登录打标机，无法打标");
                return false;
            }

            try
            {
                var docName = string.IsNullOrWhiteSpace(templatePath) ? "" : Path.GetFileName(templatePath);

                // 1. 打开模板文档，等 ODC 应答：应答里的 DC 才是设备实际认的文档名（SPI 必须用它）
                if (!string.IsNullOrWhiteSpace(templatePath))
                {
                    var openCmd = JsonSerializer.Serialize(new Dictionary<string, object> { ["F"] = "ODC", ["DP"] = templatePath }, JsonOpts);
                    _odcTcs = new TaskCompletionSource<JsonElement>();
                    Log("[TCP→] ODC 打开模板：" + openCmd + EndFlag);
                    await _client.SendAsync(new ReadOnlyMemory<byte>(_encoding.GetBytes(openCmd)), _cts.Token);

                    if (_odcAckEnabled)
                    {
                        if (await WaitAckAsync(_odcTcs, "ODC 打开模板"))
                        {
                            var odc = _odcTcs.Task.Result;
                            var dc = GetString(odc, "DC");
                            if (!string.IsNullOrWhiteSpace(dc)) docName = dc.Trim();
                            if (AckFailed(odc))
                            {
                                Log("打开模板失败：" + AckMessage(odc));
                                return false;
                            }
                            Log("模板已打开，设备返回文档名：" + docName);
                        }
                        else
                        {
                            _odcAckEnabled = false;
                            Log("该设备不回 ODC 应答，后续打印不再等待此应答，文档名按文件名继续：" + docName);
                        }
                    }
                    else
                    {
                        Log("[TCP←] 跳过 ODC 应答等待（该设备不回此应答）");
                    }
                }

                // 2. SPI 批量替换文本图元，等 SPI 应答
                if (items != null && items.Count > 0)
                {
                    var spiCmd = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["F"] = "SPI",
                        ["DC"] = docName,
                        ["P"] = items.Select(i => new { N = i.N, T = i.T }).ToList()
                    }, JsonOpts);
                    _spiTcs = new TaskCompletionSource<JsonElement>();
                    Log("[TCP→] SPI 替换图元：" + spiCmd + EndFlag);
                    await _client.SendAsync(new ReadOnlyMemory<byte>(_encoding.GetBytes(spiCmd)), _cts.Token);

                    if (_spiAckEnabled)
                    {
                        if (await WaitAckAsync(_spiTcs, "SPI 替换图元"))
                        {
                            if (AckFailed(_spiTcs.Task.Result))
                            {
                                Log("图元替换失败：" + AckMessage(_spiTcs.Task.Result));
                                return false;
                            }
                        }
                        else
                        {
                            _spiAckEnabled = false;
                            Log("该设备不回 SPI 应答，后续打印不再等待此应答");
                        }
                    }
                    else
                    {
                        Log("[TCP←] 跳过 SPI 应答等待（该设备不回此应答）");
                    }
                }

                // 3. 启动标刻
                var startCmd = "{\"F\":\"M\"}";
                Log("[TCP→] M 启动标刻：{\"F\":\"M\"}#");
                await _client.SendAsync(new ReadOnlyMemory<byte>(_encoding.GetBytes(startCmd)), _cts.Token);

                // 4. 等待设备异步推送 MS 完成报文（带超时）
                _markTcs = new TaskCompletionSource<bool>();
                var done = await Task.WhenAny(_markTcs.Task, Task.Delay(timeoutMs));
                var success = done == _markTcs.Task && _markTcs.Task.Result;
                Log(success ? "打标完成" : "打标超时/未收到完成报文");
                return success;
            }
            catch (Exception ex)
            {
                Log("打标执行异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>接收设备报文（TouchSocket 事件线程回调）：任何报文都先原样打日志，再做业务解析</summary>
        private void OnReceived(ReceivedDataEventArgs e)
        {
            var json = "";
            try
            {
                json = DecodeReceived(e.Memory.ToArray()).Trim();
                if (string.IsNullOrWhiteSpace(json)) return;

                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement.Clone();
                    var f = GetString(root, "F");
                    if (f != null) f = f.Trim();
                    var hasS = root.TryGetProperty("S", out _);
                    var s = GetInt(root, "S", -1);

                    Log("[TCP←] " + CmdName(f) + " 应答（S=" + (hasS ? s.ToString() : "无") + "）：" + json);

                    switch (f)
                    {
                        case "L": // 登录应答
                            _loginTcs?.TrySetResult(s == 1);
                            break;
                        case "ODC": // 打开模板应答
                            _odcTcs?.TrySetResult(root);
                            break;
                        case "SPI": // 图元替换应答
                            _spiTcs?.TrySetResult(root);
                            break;
                        case "MS": // 打标完成（设备主动推送）
                            var success = s == 1 || s == 0 || !hasS;
                            _markTcs?.TrySetResult(success);
                            OnMarkFinished?.Invoke(success, json);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[TCP←] 报文解析失败：" + ex.Message + "，原始内容：" + json);
                // 非 JSON 报文（如普通应答字符串），不阻塞流程
                _markTcs?.TrySetResult(true);
            }
        }

        /// <summary>等某条指令的应答。超时只记日志并返回 false，不判失败（部分板卡对 ODC/SPI 不回包）</summary>
        private async Task<bool> WaitAckAsync(TaskCompletionSource<JsonElement> tcs, string cmdName)
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(AckTimeoutMs));
            if (done == tcs.Task) return true;
            Log("[TCP←] 未收到 " + cmdName + " 的应答（已等待 " + AckTimeoutMs + " ms），继续执行后续指令");
            return false;
        }

        /// <summary>解码设备返回的报文</summary>
        private string DecodeReceived(byte[] data) => _encoding.GetString(data);

        /// <summary>取字符串字段（字段不存在返回 null）</summary>
        private static string GetString(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty(name, out var el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }

        /// <summary>取整数字段（协议里 S 偶尔是字符串，两种都接）</summary>
        private static int GetInt(JsonElement root, string name, int defaultValue)
        {
            if (root.ValueKind != JsonValueKind.Object) return defaultValue;
            if (!root.TryGetProperty(name, out var el)) return defaultValue;

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
            if (el.ValueKind == JsonValueKind.String)
            {
                int parsed;
                if (int.TryParse(el.GetString(), out parsed)) return parsed;
            }
            return defaultValue;
        }

        /// <summary>应答里的 S 是否为 0（0 表示设备执行失败）</summary>
        private static bool AckFailed(JsonElement ack)
        {
            return GetInt(ack, "S", -1) == 0;
        }

        /// <summary>取应答里的 MSG 说明</summary>
        private static string AckMessage(JsonElement ack)
        {
            var msg = GetString(ack, "MSG");
            return string.IsNullOrEmpty(msg) ? "（设备未返回 MSG）" : msg;
        }

        /// <summary>指令编码 -> 中文名，仅用于日志可读性</summary>
        private static string CmdName(string f)
        {
            switch (f)
            {
                case "L": return "L 登录";
                case "ODC": return "ODC 打开模板";
                case "SPI": return "SPI 替换图元";
                case "M": return "M 启动标刻";
                case "MS": return "MS 标刻完成";
                case "GADS": return "GADS 获取设备";
                case "GADC": return "GADC 获取文档";
                case "SDC": return "SDC 保存文档";
                case "CDC": return "CDC 关闭文档";
                case "ST": return "ST 停止标刻";
                case "GMS": return "GMS 查询标刻状态";
                default: return string.IsNullOrEmpty(f) ? "未知指令" : f;
            }
        }

        /// <summary>断开连接</summary>
        public async Task DisconnectAsync()
        {
            try
            {
                if (_client != null && _client.Online)
                    await _client.CloseAsync("主动断开", _cts.Token);
            }
            catch (Exception ex)
            {
                Log("断开异常：" + ex.Message);
            }
        }

        private void Log(string msg)
            => OnLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                _client?.Dispose();
            }
            catch { /* 忽略释放异常 */ }
        }
    }
}
