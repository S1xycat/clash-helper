# Clash Float Status

一个贴合 Windows 11 任务栏的 Clash 状态条，显示系统代理开关、当前节点地区和延迟。

## 功能

- 常驻任务栏附近，不占用普通任务栏按钮
- 左键点击开关即可开启/关闭 Windows 系统代理
- 自动读取 Clash 当前选择节点，显示地区
- 每 10 秒测一次当前节点延迟
- 右键菜单支持锁定位置、解锁位置、立即刷新、开机启动、退出
- 位置自动记忆，解锁后可拖动并吸附任务栏
- 真全屏游戏或视频时自动隐藏，退出全屏后自动恢复
- 使用分层透明窗口，透明背景下开关和文字边缘更干净

## 下载使用

从 Release 下载 `ClashFloatStatus.exe`，双击运行即可。

推荐先启动 Clash for Windows / Clash Verge / Mihomo Party 等 Clash 客户端，再启动本工具。

## 右键菜单

- `解锁位置 / 锁定位置`：控制是否允许拖动
- `立即刷新`：重新读取当前节点并测试延迟
- `开机启动`：切换本工具是否随 Windows 登录启动
- `退出`：关闭本工具

## 兼容说明

本工具优先读取正在运行的 Clash for Windows 核心配置，也会尝试读取常见 Clash 配置路径：

- `%USERPROFILE%\.config\clash\config.yaml`
- `%APPDATA%\clash\config.yaml`
- Clash for Windows 运行目录下的 `data\config.yaml`

当前节点识别优先级：

- `🔰 选择节点`
- `🚀 节点选择`
- `节点选择`
- `Proxy`
- `代理`
- `GLOBAL`

如果你的配置使用了别的策略组名称，可以在 `src/Program.cs` 里调整 `preferred` 列表。

## 构建

本项目使用系统自带 .NET Framework C# 编译器构建，不需要安装 .NET SDK。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

生成文件：

```text
bin\ClashFloatStatus.exe
```

## 数据位置

本工具的窗口位置和锁定状态保存在：

```text
%APPDATA%\ClashFloatStatus\settings.ini
```

开机启动写入：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ClashFloatStatus
```

## 注意

Windows 11 没有官方接口允许第三方控件真正嵌入任务栏右侧区域。本工具采用透明分层窗口贴合任务栏，尽量模拟原生任务栏体验，同时避免注入 `explorer.exe` 带来的稳定性和安全风险。

