# QuickSidebar —— 桌面右侧快速启动侧栏

一个常驻桌面右侧的原生 WPF 小组件：上 5 格自动记录**最近访问**的应用，下 5 格是**快捷访问**（手动固定），点击图标即可启动或切换到对应应用。视觉与动效遵循 [emilkowalski/skills](https://github.com/emilkowalski/skills) 的设计工程规范（强 ease-out 曲线、按压缩放反馈、错峰入场、只动 transform/opacity 等）。

## 功能

- **最近访问（上 5 格）**：每 0.6 秒轮询前台窗口，自动把最近使用的应用按 MRU 排列；图标下方的小圆点表示应用正在运行。
- **快捷访问（下 5 格）**：右键空槽位或直接左键点击「+」选择 `.exe` / `.lnk`；右键已固定的图标可打开、更换或移除；也可把最近访问的应用「固定到快捷访问」。首次运行预置资源管理器、Edge、记事本、计算器、CMD（可自行修改）。
- **点击启动**：应用未运行则启动；已在运行则将其主窗口还原并置前。`.lnk` 快捷方式会先解析目标 exe 再检测（带参数或指向 explorer 的快捷方式、`.url`/`.bat` 始终直接启动）；窗口查找先按 exe 全路径精确匹配，再按进程名兜底。
- **拖动**：按住面板顶部拖动条可把面板放到屏幕任意位置，位置自动记忆。
- **最小化**：点击面板右上角的 `‹` 按钮，长条收起为一个小圆圈；点击圆圈恢复，面板从圆圈处展开（空间一致）。
- **右键面板空白处**：开机自启开关（写入 `HKCU\...\Run`）、退出。
- 单实例互斥；配置持久化于 `%LOCALAPPDATA%\QuickSidebar\config.json`（位置、最小化状态、最近访问、快捷访问），采用临时文件原子写入，进程被强杀也不会损坏。

## 构建

无需安装任何 SDK，用 Windows 自带的 .NET Framework 4.8 编译器：

```cmd
build.cmd
```

产物为单文件 `QuickSidebar.exe`（约 45 KB，冷启动 < 0.2 秒）。

## 运行

双击 `QuickSidebar.exe` 即可；勾选「开机自启」后无需手动启动。异常会记录到 `%LOCALAPPDATA%\QuickSidebar\error.log`。

## 文件

| 文件 | 说明 |
| --- | --- |
| `Program.cs` | 全部源码（C# 5，WPF code-only，无 XAML 文件） |
| `app.manifest` | DPI 感知（PerMonitorV2）与 OS 兼容声明 |
| `build.cmd` | 一键构建脚本（调用内置 csc.exe） |
