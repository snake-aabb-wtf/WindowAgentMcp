# WindowAgentMcp 使用指南

本文说明如何在 Windows 上构建、配置并使用本地窗口选择器和 MCP 服务。

## 系统要求

- Windows 10 版本 1903（Build 18362）或更新版本。
- Windows x64。
- 构建时使用仓库内安装的 .NET 10 x64 SDK；发布后的自包含程序不要求另装 .NET Runtime。
- Codex 或其他支持 MCP stdio 的客户端。

## 构建与发布

在仓库根目录的 PowerShell 中运行：

```powershell
.\eng\install-dotnet.ps1
.\eng\build.ps1
.\eng\publish.ps1
```

发布文件位于：

```text
artifacts\publish\win-x64\WindowAgentMcp.exe
```

## 配置 Codex MCP

把已发布 EXE 的绝对路径加入 Codex MCP 配置。PowerShell 命令示例：

```powershell
codex mcp add window-agent -- "C:\Path\To\WindowAgentMcp.exe"
codex mcp list
```

如果仓库位于其他目录，请替换成实际路径。配置更新后，重启或重新加载 MCP 客户端，使它重新启动服务进程。

## 选择并连接窗口

MCP 服务启动和工具清单查询不会显示窗口。操作步骤：

1. 让 Agent 调用 `show_window_picker`，打开本地窗口选择器。
2. 在列表中找到要交给 Agent 操作的可见顶层窗口，选中并点击 **连接**。
3. 确认状态栏显示目标窗口标题、进程名和 PID。
4. 让 Agent 调用 `screenshot`。没有连接目标时，截图或输入工具会自动打开选择器并返回 `no_window_selected`，此时先完成人工连接再重试。

目标窗口关闭或其独立对话框需要单独操作时，请在选择器中刷新列表并重新连接。MCP 工具无法自行更换目标窗口。

## Agent 的操作循环

每次操作按以下顺序进行：

1. 调用 `screenshot` 获取当前目标窗口的 PNG 和宽高。
2. 查看图像，使用图像左上角为原点的客户区像素坐标。
3. 调用一个鼠标或键盘工具。
4. 再次截图，确认界面变化后决定下一步。

窗口大小或 DPI 改变后，应先重新截图，再使用新坐标。工具报告输入已发送，并不代表目标应用一定完成了预期操作。

常用工具：

| 工具 | 用途 |
| --- | --- |
| `show_window_picker` | 打开本地选择器，供人类选择目标 |
| `screenshot` | 获取当前绑定窗口的截图 |
| `click` | 左键或右键单击/双击 |
| `mouse_down` / `mouse_up` | 按住并释放鼠标键 |
| `drag` | 在两个客户区坐标间拖动 |
| `move_mouse` / `scroll` | 移动指针或滚动 |
| `type_text` | 输入 Unicode 文本 |
| `key_press` | 点击命名按键，可带 Ctrl、Alt、Shift |
| `key_down` / `key_up` | 按住并释放命名按键 |

完整参数、范围和错误码见仓库根目录的 [README](../README.md)。

## 断开与退出

点击选择器的 **断开** 会解除目标绑定。关闭选择器或 MCP stdio 客户端退出时，服务会尝试释放由它按住的鼠标键和键盘键，并清理捕捉资源。

## 权限与捕捉限制

- 程序不会自动以管理员身份运行。
- Windows UIPI 可能阻止普通权限程序向更高完整性级别的目标发送输入；让服务和目标应用以兼容权限运行。
- DRM 内容、安全桌面和受系统保护的画面可能无法捕捉。
- 单独弹出的顶层对话框需要重新选择。
- 截图会返回给 MCP 客户端，但默认不写入磁盘；日志不记录截图内容或 `type_text` 文本。
