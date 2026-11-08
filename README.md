# LabelMaker

把八思量打标软件的 TCP 协议封装成本机 HTTP 接口，供 MES、网页前端或脚本直接调用。

打标软件对外只有 TCP（板卡的 JSON 协议），而 MES 和浏览器更适合走 HTTP。这个小工具常驻托盘，在连着打标机的那台电脑上把设备能力翻成一个 POST 接口，调用方不需要关心 TCP 连接和协议细节。

## 适用场景

- 打标软件只提供 TCP，没有 HTTP 接口
- MES 或网页需要触发打标，不想在服务端维护设备长连接
- 一台电脑固定连一台打标机，部署在产线现场

## 快速开始

1. 安装 .NET Framework 4.8（Win10/11 通常自带）
2. 用 Visual Studio 打开 `LabelMaker.sln` 编译，产物为 `bin\Release\LabelMaker.exe`
3. 首次运行会在 exe 同目录生成 `config.json`，按现场情况修改
4. 双击 exe，托盘出现图标即表示服务已启动
5. 打开 `http://127.0.0.1:9623/ping`，返回 `{"code":200,"message":"pong"}` 说明正常

托盘图标右键可打开「设置」「日志目录」「关于」。日志按天写在 exe 同目录 `logs\`。

## 配置项（config.json）

| 字段 | 说明 | 默认值 |
| --- | --- | --- |
| LaserIp | 打标机 IP | 127.0.0.1 |
| LaserPort | 打标机 TCP 端口 | 7890 |
| LaserPassword | 打标机登录密码 | 123456 |
| TemplatePath | .orzx 模板绝对路径，请求未指定模板时使用 | D:\template\label.orzx |
| WebPort | HTTP 服务端口 | 9623 |
| WebAllowLan | 是否允许局域网访问；false 时只监听 127.0.0.1 | false |
| MarkTimeoutMs | 等待打标完成的超时时间（毫秒） | 30000 |

## HTTP 接口

### GET /ping

探活，返回 `{"code":200,"message":"pong"}`。

### POST /print

提交一次打标任务，同步返回结果——打标完成后才响应。

请求体：

```json
{
  "templatePath": "D:\\template\\label.orzx",
  "items": [
    { "N": 0, "T": "TZ-1005" },
    { "N": 1, "T": "描述字段内容" }
  ]
}
```

- `templatePath` 可省略，省略时用配置里的 `TemplatePath`
- `items` 为模板图元替换列表：`N` 是图元序号，`T` 是写入的文本
- `items` 上限 500 项，请求体上限 512KB

响应体统一为 `{"code":<状态码>,"message":"<说明>"}`：

| HTTP | 含义 |
| --- | --- |
| 200 | 打标完成 |
| 400 | 请求参数不合法 |
| 409 | 上一条还没打完，设备正忙 |
| 500 | 连接失败或打标失败 |

调用示例：

```bash
curl -X POST http://127.0.0.1:9623/print \
  -H "Content-Type: application/json" \
  -d "{\"items\":[{\"N\":0,\"T\":\"TIZA-1005\"}]}"
```