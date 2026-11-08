using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LabelMaker.Devices;

namespace LabelMaker.Services
{
    /// <summary>一次 HTTP 打印请求</summary>
    public class PrintJob
    {
        /// <summary>模板路径（为空则用配置里的 TemplatePath）</summary>
        public string TemplatePath { get; set; }

        /// <summary>图元替换项，例如 [{"N":0,"T":"123"}]</summary>
        public List<PlotTextItem> Items { get; set; } = new List<PlotTextItem>();
    }

    /// <summary>打印结果</summary>
    public class PrintResult
    {
        public bool Success { get; set; }
        /// <summary>打标机正忙（上一条还没打完）</summary>
        public bool Busy { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// 本地 HTTP 打标接口（基于 .NET 自带的 HttpListener，无第三方依赖）：
    ///
    ///   GET  /ping     -> {"code":200,"message":"pong"}
    ///   POST /print    -> {"templatePath":"D:\\template\\label.orzx",
    ///                      "items":[{"N":0,"T":"123"},{"N":1,"T":"456"}]}
    ///   OPTIONS *      -> 204（供浏览器跨域预检）
    ///
    /// 说明：
    ///  - 返回体固定是 {"code":…,"message":…}，**code 与 HTTP 状态码一致**
    ///    （200 成功 / 400 参数错 / 409 忙 / 500 失败 / 405 / 404 / 413 / 503）；
    ///  - 不鉴权，默认只监听 127.0.0.1，本机网页/脚本可直接调用；
    ///  - 需要局域网其它机器调用时，勾选「允许局域网访问」，此时通常要管理员身份
    ///    运行，或先执行 netsh http add urlacl；
    ///  - 一次只打一张，重复请求会返回 409（打标机正忙）。
    /// </summary>
    public class LocalPrintServer : IDisposable
    {
        private readonly object _gate = new object();
        private HttpListener _listener;
        private volatile bool _running;

        /// <summary>实际监听端口</summary>
        public int Port { get; private set; }

        /// <summary>是否允许局域网访问</summary>
        public bool AllowLan { get; private set; }

        /// <summary>供前端/脚本引用的地址，形如 http://127.0.0.1:8723</summary>
        public string BaseUrl
        {
            get
            {
                if (!_running) return null;
                return AllowLan ? "http://本机IP:" + Port : "http://127.0.0.1:" + Port;
            }
        }

        public bool IsRunning => _running;

        /// <summary>日志输出</summary>
        public event Action<string> OnLog;

        /// <summary>宿主注入的打印实现</summary>
        public Func<PrintJob, Task<PrintResult>> PrintHandler { get; set; }

        /// <summary>启动服务</summary>
        public bool Start(int port, bool allowLan, out string error)
        {
            error = null;
            lock (_gate)
            {
                if (_running) return true;
                if (port <= 0 || port > 65535)
                {
                    error = "端口无效（1-65535）";
                    return false;
                }

                var prefix = allowLan ? "http://+:" + port + "/" : "http://127.0.0.1:" + port + "/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                }
                catch (Exception ex)
                {
                    try { listener.Close(); } catch { }
                    error = allowLan
                        ? "启动失败：" + ex.Message + "。局域网模式需要以管理员身份运行，或先执行：\r\n" +
                          "netsh http add urlacl url=http://+:" + port + "/ user=Everyone"
                        : "启动失败：" + ex.Message + "（端口 " + port + " 可能已被其它程序占用）";
                    return false;
                }

                _listener = listener;
                Port = port;
                AllowLan = allowLan;
                _running = true;

                var loop = new Task(() => AcceptLoop(listener), TaskCreationOptions.LongRunning);
                loop.Start();

                Log("HTTP 服务已启动：" + BaseUrl + (allowLan ? "（允许局域网访问）" : "（仅本机访问）"));
                return true;
            }
        }

        /// <summary>停止服务</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
                _listener = null;
                Log("HTTP 服务已停止");
            }
        }

        private void AcceptLoop(HttpListener listener)
        {
            while (_running && listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext(); // 同步阻塞，本方法跑在独立长线程里
                }
                catch
                {
                    break; // Stop 时正常中断
                }

                var ignored = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                // 浏览器前端跨域调用必带（不鉴权，本机使用）
                resp.Headers["Access-Control-Allow-Origin"] = "*";
                resp.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                resp.Headers["Cache-Control"] = "no-store";

                var path = req.Url == null ? "/" : req.Url.AbsolutePath;
                if (path.Length > 1) path = path.TrimEnd('/');
                if (path.Length == 0) path = "/";

                if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = 204;
                    return;
                }

                if (path == "/ping")
                {
                    // 探活：GET / POST 都接受，其它方法（PUT / DELETE 等）一律拒绝
                    if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(resp, 405, "请使用 GET 或 POST 调用 /ping");
                        return;
                    }
                    WriteJson(resp, 200, "pong");
                    return;
                }

                if (path == "/print")
                {
                    // 必须 await：打印要连设备、等标刻完成，可能几秒到几十秒，
                    // 提前返回会让下面 finally 里的 resp.Close() 把响应关掉，客户端就收不到任何数据
                    await HandlePrint(req, resp);
                    return;
                }

                WriteJson(resp, 404, "未知接口：可用 GET /ping、POST /print");
            }
            catch (Exception ex)
            {
                try { WriteJson(resp, 500, ex.Message); } catch { }
            }
            finally
            {
                try { resp.Close(); } catch { }
            }
        }

        private async Task HandlePrint(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    WriteJson(resp, 405, "请使用 POST 调用 /print");
                    return;
                }

                string body;
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    body = reader.ReadToEnd();

                if (body.Length > 512 * 1024)
                {
                    WriteJson(resp, 413, "请求体过大（上限 512KB）");
                    return;
                }

                PrintJob job;
                string error;
                if (!TryParseJob(body, out job, out error))
                {
                    Log("HTTP 请求解析失败：" + error);
                    WriteJson(resp, 400, error);
                    return;
                }

                var handler = PrintHandler;
                if (handler == null)
                {
                    WriteJson(resp, 503, "打印模块未就绪");
                    return;
                }

                Log("收到 HTTP 打印请求：模板=" + (job.TemplatePath ?? "(用默认)") + "，图元 " + job.Items.Count + " 项");
                var result = await handler(job);

                if (result.Busy)
                {
                    Log("HTTP 打印结果：打标机正忙");
                    WriteJson(resp, 409, result.Message);
                }
                else if (result.Success)
                {
                    Log("HTTP 打印结果：" + result.Message);
                    WriteJson(resp, 200, result.Message);
                }
                else
                {
                    Log("HTTP 打印失败：" + result.Message);
                    WriteJson(resp, 500, result.Message);
                }
            }
            catch (Exception ex)
            {
                Log("HTTP 打印异常：" + ex.Message);
                try { WriteJson(resp, 500, "服务异常：" + ex.Message); } catch { }
            }
            finally
            {
                try { resp.Close(); } catch { }
            }
        }

        /// <summary>解析请求体；items 支持真数组，也支持「数组的字符串」两种写法</summary>
        private static bool TryParseJob(string body, out PrintJob job, out string error)
        {
            job = null;
            error = null;

            if (string.IsNullOrWhiteSpace(body))
            {
                error = "请求体为空";
                return false;
            }

            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        error = "请求体应为 JSON 对象，例如 {\"templatePath\":\"...\",\"items\":[...]}";
                        return false;
                    }

                    var parsed = new PrintJob();

                    if (root.TryGetProperty("templatePath", out var tp) && tp.ValueKind == JsonValueKind.String)
                    {
                        var p = tp.GetString();
                        parsed.TemplatePath = string.IsNullOrWhiteSpace(p) ? null : p.Trim();
                    }

                    if (!root.TryGetProperty("items", out var itemsEl))
                    {
                        error = "缺少 items（图元替换数组），例如 [{\"N\":0,\"T\":\"123\"}]";
                        return false;
                    }

                    JsonDocument innerDoc = null;
                    try
                    {
                        // 兼容 items 传字符串： "items":"[{\"N\":0,\"T\":\"123\"}]"
                        var arrEl = itemsEl;
                        if (itemsEl.ValueKind == JsonValueKind.String)
                        {
                            innerDoc = JsonDocument.Parse(itemsEl.GetString() ?? "[]");
                            arrEl = innerDoc.RootElement;
                        }

                        if (arrEl.ValueKind != JsonValueKind.Array)
                        {
                            error = "items 应为 JSON 数组";
                            return false;
                        }

                        var index = 0;
                        foreach (var item in arrEl.EnumerateArray())
                        {
                            if (index >= 500)
                            {
                                error = "items 数量过多（上限 500）";
                                return false;
                            }
                            index++;

                            if (item.ValueKind != JsonValueKind.Object)
                            {
                                error = "items 第 " + index + " 项不是对象，应为 {\"N\":0,\"T\":\"...\"}";
                                return false;
                            }

                            JsonElement ne;
                            if (!item.TryGetProperty("N", out ne) && !item.TryGetProperty("n", out ne))
                            {
                                error = "items 第 " + index + " 项缺少 N";
                                return false;
                            }

                            int n;
                            if (!int.TryParse(ToText(ne), out n) || n < 0)
                            {
                                error = "items 第 " + index + " 项的 N 必须是非负整数";
                                return false;
                            }

                            JsonElement te;
                            var text = item.TryGetProperty("T", out te) || item.TryGetProperty("t", out te)
                                ? ToText(te)
                                : string.Empty;

                            parsed.Items.Add(new PlotTextItem { N = n, T = text });
                        }
                    }
                    finally
                    {
                        innerDoc?.Dispose();
                    }

                    job = parsed;
                    return true;
                }
            }
            catch (JsonException ex)
            {
                error = "JSON 解析失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>把 JSON 值转成文本（数字保留原样，不出现 1E+06 之类的科学计数）</summary>
        private static string ToText(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number: return el.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return string.Empty;
                default: return el.GetRawText();
            }
        }

        private static void WriteJson(HttpListenerResponse resp, int httpStatus, string message)
        {
            var text = "{\"code\":" + httpStatus + ",\"message\":" + Escape(message) + "}";
            var bytes = Encoding.UTF8.GetBytes(text);
            resp.StatusCode = httpStatus;
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = bytes.Length;
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.OutputStream.Flush();
        }

        private static string Escape(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private void Log(string msg) => OnLog?.Invoke(msg);

        public void Dispose() => Stop();
    }
}
