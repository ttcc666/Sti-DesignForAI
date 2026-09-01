# STI 标签工位

Windows 桌面客户端：中间标签画布，左边对话，右边字段和本机打印机。第一期对话走本机规则助手，不调用外网。数据用 SqlSugar + SQLite，库文件在 `%AppData%\StiLabel\`。

## 要求

- .NET 10 SDK
- Windows 10/11

## 运行

```powershell
dotnet run --project src/StiLabel.App/StiLabel.App.csproj
```

## 解决方案

| 项目 | 职责 |
| --- | --- |
| `StiLabel.Core` | Label IR、页规格、规则助手、草稿编译 |
| `StiLabel.Data` | SqlSugar、字段字典、最近文件、版本、示例数据 |
| `StiLabel.App` | WPF 三栏工作台、预览打样 |

## 第一期能做的

- 新建 / 打开 / 保存 `.label.json`（打开 `.mrt` 时读旁边的 `.label.json`）
- 勾选字段生成物料标签草稿
- 对话：出草稿、加字段、分析、拒绝字典外字段
- 本机打印机列表、预览打样
- 另存一版到版本目录

中间栏已嵌入 Stimulsoft 官方设计器。许可文件放到 `%AppData%\StiLabel\license.key`。

对话按官网接入：OpenAI Chat Completions / Responses、Anthropic Messages、Gemini generateContent、Azure OpenAI、Ollama 原生。在「模型设置」选厂商、确认格式和模型名，可测连通。关闭后对话禁用，设计与打印仍可用。
