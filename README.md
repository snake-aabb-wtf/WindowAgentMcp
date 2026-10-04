# WindowAgentMcp

[![Windows CI](https://github.com/snake-aabb-wtf/WindowAgentMcp/actions/workflows/ci.yml/badge.svg)](https://github.com/snake-aabb-wtf/WindowAgentMcp/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/snake-aabb-wtf/WindowAgentMcp)](https://github.com/snake-aabb-wtf/WindowAgentMcp/releases/latest)

[MIT 许可证](LICENSE)，Windows x64。

一个仅支持 Windows 的本地 MCP stdio 服务。Agent 调用 `show_window_picker` 后会打开本地窗口选择器；用户选择并连接一个窗口后，Agent 可以按“截图 → 看图 → 操作 → 再截图”的循环使用 MCP 工具。没有连接目标时，首次截图或输入请求也会自动打开选择器并返回 `no_window_selected`。

MCP 请求不能传入 HWND，也不能切换目标窗口。目标绑定只由本地选择器完成。

## 平台要求

- Windows 10 版本 1903（Build 18362）或更新版本
- Windows x64
- .NET 10 SDK 用于构建；发布版自包含，不需要另外安装 .NET Runtime

窗口捕捉使用 Windows Graphics Capture 的 HWND 接口。DRM/安全桌面/系统保护表面可能无法捕捉；某些独立弹出的对话框需要在选择器中重新选为目标。程序以当前用户权限运行，不会自动提权；Windows UIPI 可能阻止向更高完整性级别的进程发送输入。

## 安装本地 SDK 与构建

SDK 和 NuGet 缓存都保存在仓库内的 `.tools\` 目录，不修改系统 PATH，也不做全局安装。

```powershell
.\eng\install-dotnet.ps1
.\eng\build.ps1
```

发布自包含的单文件 `win-x64` 程序：

```powershell
.\eng\publish.ps1
```

输出位置：

```text
artifacts\publish\win-x64\WindowAgentMcp.exe
```

## MCP 客户端配置

将 MCP 客户端配置为启动发布后的 EXE，并使用 stdio。下面路径按本仓库位置填写；移动文件后请改成实际绝对路径。

```json
{
  "mcpServers": {
    "window-agent": {
      "command": "C:\\Path\\To\\WindowAgentMcp.exe"
    }
  }
}
```

服务启动和工具发现时保持选择器隐藏，避免客户端查询工具清单时反复闪窗。让 Agent 调用 `show_window_picker`，再由用户选中可见顶层窗口并点 **连接**。关闭选择器或 MCP stdio 客户端断开时，服务退出并尝试释放按住的键和鼠标键。MCP 协议只使用 stdout；诊断日志写到 stderr 和 `%LOCALAPPDATA%\WindowAgentMcp\logs`。

## 坐标和工具

鼠标坐标使用 `screenshot` 返回图像的客户区物理像素：左上角为 `(0, 0)`，有效范围是 `0 <= x < width`、`0 <= y < height`。窗口尺寸或 DPI 改变后，先重新截图，再使用新坐标。超出边界和过期坐标不会发送输入。

| 工具 | 用途 |
| --- | --- |
| `show_window_picker` | 显示本地选择器，等待人类选择并连接目标；不自动选择窗口 |
| `screenshot` | 返回 PNG、目标标题、客户区宽高和坐标说明 |
| `move_mouse` | 移动指针；`duration_ms` 为 0–5000，默认 0 |
| `click` | 左/右键单击或双击；`click_count` 为 1 或 2 |
| `mouse_down` / `mouse_up` | 按住和释放左/右键；可跨请求组合成长按 |
| `drag` | 在两个客户区坐标之间拖动；`duration_ms` 为 50–10000，默认 500 |
| `scroll` | 垂直/水平滚动；每单位为一个标准滚轮刻度 |
| `type_text` | 输入 Unicode 文本，最多 100000 个 UTF-16 code unit |
| `key_press` | 按下命名按键，可带 Ctrl、Alt、Shift 修饰键 |
| `key_down` / `key_up` | 按住和释放本服务支持的命名按键 |

支持字母、数字、F1–F24、常见导航/控制键和部分标点键名。Windows 键不在键目录中；Alt+Tab、Alt+Esc、Alt+F4、Ctrl+Esc、Ctrl+Shift+Esc 和 Ctrl+Alt+Delete 等系统快捷键会被拒绝。重复按下已由本服务按住的键/按钮，或释放未由本服务按住的键/按钮，会返回状态错误。所有显式按住状态会在 60 秒后由看门狗尝试释放。

输入工具只报告输入事件是否已发送，不判断应用内部动作是否成功；Agent 应使用下一次截图确认结果。`input_incomplete` 表示 Windows 没有接受全部输入事件，并提示权限级别可能不兼容；Windows 不一定能指出 UIPI 是否具体为原因。

## 隐私与限制

- 截图只放在 MCP 响应中，不默认保存到磁盘。
- 日志不记录截图像素或 `type_text` 的文本内容。
- `SendInput` 是系统级输入流，无法提供 HWND 级硬隔离；服务会在操作前核对绑定窗口身份和前台状态，但无法消除 Windows 焦点变化竞态。
- 不跟踪目标进程另外创建的顶层窗口；如需操作独立对话框，请在本地选择器中重新绑定。
- Windows 可能因权限级别（UIPI）拒绝向更高完整性级别进程注入输入。让 MCP 服务和目标应用以兼容的权限级别运行；程序不会自动提权。

## 深入文档

- [使用指南](docs/USER_GUIDE.md)：构建、配置 Codex MCP、选择目标窗口和操作循环。
- [故障排查](docs/TROUBLESHOOTING.md)：日志位置、生命周期事件和常见截图/输入问题。
- [架构与诊断说明](docs/ARCHITECTURE_AND_DIAGNOSTICS.md)：组件、MCP 生命周期、日志字段与已知限制。
- [2026-10-04 闪窗定位记录](docs/diagnostics/2026-10-04-selector-flicker.md)：宿主日志、重现过程和修复依据。

## 下载

从 [GitHub Releases](https://github.com/snake-aabb-wtf/WindowAgentMcp/releases/latest) 下载 `WindowAgentMcp.exe`。这是自包含的 Windows x64 单文件程序，不需要单独安装 .NET Runtime。
