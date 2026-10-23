# Legacy PowerShell 入口

新用户请使用 README 中的原生 .NET 入口。旧脚本保留用于已有安装的兼容与实现参考，不承诺仅克隆本仓库即可发送通知。

| 入口 | 用途 |
| --- | --- |
| `CodexFeishuBridge/Start-CodexFeishuTray.vbs` | 启动旧托盘 |
| `CodexFeishuBridge/Start-CodexFeishuSettings.vbs` | 打开旧设置窗口 |
| `CodexFeishuBridge/Open-CodexFeishuConsole.vbs` | 以可见 PowerShell 进程打开旧设置窗口 |

三个启动器均相对于自身文件定位脚本，不依赖开发者的绝对目录。

旧托盘与设置窗口会调用 `%USERPROFILE%\.codex\notifications\codex-feishu-notify.ps1`。**该文件是原有安装的外部组件，本公开仓库不包含它。** 缺少它时，旧版通知发送不可用；原生版无需这个脚本。

旧版使用脚本目录中的 `notification-settings.json`、`runtime-status.json`、`notifications.log` 和 `captures/`，均不进入 Git。字段 `CompletionEnabled` / `FailureEnabled` 与原生版的 `CompletionNotifications` / `FailureNotifications` 不同，两套设置不会自动迁移，原生版示例配置不适用于旧版。

旧版首次使用时通过设置窗口创建密码；首次设置的“当前密码”输入框禁用，取消则不启动托盘。已有设置及密码保持原样，不会因为源码中的固定默认口令移除而被重写。

不要同时运行两种托盘入口，以免产生重复通知、快捷键冲突或两套防护状态。旧版也需要 Windows PowerShell 5.1；可选拍照需要用户自行安装 FFmpeg。共享输入检测见 [input-detection.md](input-detection.md)。
