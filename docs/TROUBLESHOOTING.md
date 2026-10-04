# WindowAgentMcp 故障排查

## 选择器不断闪现或重复打开

旧版本在每个 `WindowAgentMcp.exe` 进程启动时立即显示选择器。已确认 Codex 的 `mcpServerStatus/list` 状态查询会建立临时 MCP 连接，并在查询完成后结束这个临时服务进程，因此旧版选择器会跟着反复出现。正常聊天使用的服务进程可以同时保持运行。

修复后的版本在启动和工具发现期间隐藏选择器，只有 `show_window_picker` 或未选目标的实际操作请求才打开 GUI。若仍然看到闪窗，先确认 MCP 客户端已经重载到修复后的 EXE；旧进程不会因磁盘上的代码变化自动更新。

先检查进程日志：

```text
%LOCALAPPDATA%\WindowAgentMcp\logs\
```

新文件名格式为 `window-agent-YYYYMMDD-HHMMSSfff-PID.log`，启动时间与 PID 共同区分进程运行。目录中的旧格式 `window-agent-YYYYMMDD-PID.log` 可能包含 PID 复用后的多次运行；阅读旧文件时按重复的 `event=app.starting` 分段。

### 常见事件顺序

正常启动会依次看到类似内容：

```text
event=app.starting
event=selector.created visibility=hidden
event=mcp.server_task.started
... transport reading messages
... Application started
event=mcp.host.started
```

客户端正常关闭 stdio 后，预期还能看到 transport 完成读取、`mcp.host.stopped`、`mcp.server_task.completed`、`app.shutdown_requested` 以及 `app.exiting` 等关闭事件。事件细节可能随 MCP SDK 版本变化。

实际打开选择器时另外记录 `selector.requested` 与 `selector.shown`。如果日志停在 `mcp.host.started`，却没有异常或关闭事件，进程可能被宿主直接结束。Codex 的临时状态查询进程就会出现这种模式；需要对照宿主日志中的 `mcpServerStatus/list`，不能只凭缺少退出事件判断应用崩溃。

### 本机 2026-10-04 的一次观测

重启 MCP 后，在约 19:56:51–19:57:21（本地时间）观察到多个不同 PID 启动。它们都成功显示选择器并启动 MCP 主机，日志没有应用异常，也没有记录正常关闭；当时还有一个 18:04 启动的旧进程仍存活。Windows 应用程序错误事件中没有匹配的 `WindowAgentMcp` 崩溃记录，Codex MCP 列表中只有一条启用的 `window-agent` 配置。

随后对照 Codex 宿主日志：45 次短命进程启动均落在 `mcpServerStatus/list` 请求期间，40 次最后一条日志距请求返回不足 1 秒。使用隔离的 Codex app-server 连续请求两次状态查询，分别出现两个临时服务进程，均在响应后消失；两次请求都成功返回工具清单。独立 MCP 初始化、工具发现和关闭也正常完成。根因是临时工具发现触发了旧版无条件显示 GUI 的启动流程。详见 [定位记录](diagnostics/2026-10-04-selector-flicker.md)。

## MCP 工具返回 `no_window_selected`

在本地选择器中选中目标并点击 **连接**。连接状态只由本地选择器管理，Agent 不能通过 MCP 工具传入 HWND 或选择另一个目标。

## 截图失败或画面为空

- 确认目标窗口仍打开且未最小化。
- 对 DRM、安全桌面或受保护表面，Windows 可能不提供可捕捉画面。
- 对尺寸或 DPI 刚发生变化的窗口，重新截图以刷新捕捉尺寸和坐标。
- 对独立弹出的对话框，在选择器中把对话框本身设为目标。

## 鼠标或键盘操作没有生效

- 重新截图并基于最新图像计算坐标；坐标使用客户区物理像素，左上角为 `(0, 0)`。
- 确认 Agent 操作的确是已连接窗口，并且目标当前允许前台输入。
- 如果目标以管理员身份运行，尝试让 MCP 服务和目标处于相同权限级别。程序不会自动提权；Windows 也不总能说明输入失败是否由 UIPI 导致。
- 检查工具错误码。`out_of_bounds` 表示坐标超出当前截图范围，`stale_coordinates` 表示窗口尺寸或 DPI 已变化，`focus_failed` 表示无法确认目标处于前台，`input_incomplete` 表示系统没有接受全部输入事件。
- 使用后续截图验证动作结果。输入事件已发送不等于应用状态已改变。

## 查看 MCP 配置

运行：

```powershell
codex mcp list
```

确认 `window-agent` 指向实际使用的发布 EXE，并且状态为 enabled。常规发布目录是 `artifacts\publish\win-x64`；本次修复的独立发布目录为 `artifacts\publish\win-x64-lifecycle`。修改路径后重载 MCP 客户端。

## 日志隐私

诊断日志用于记录启动、传输和关闭事件以及异常。日志不记录截图像素或 `type_text` 的文本内容。向他人提供日志前，可以先检查路径、进程 ID 和异常消息中是否包含本机信息。
