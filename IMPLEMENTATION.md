# 实现约定

采用 C# 5 / .NET Framework 4.x / Windows Forms，编译器为 Windows 自带 Framework64 csc。无第三方依赖，WinExe，x64。

## 核心接口（根代理负责 src/Core.cs、src/NvidiaPlatform.cs）
namespace ReplayKeeper
- enum ReplayState { Unknown, On, Off }
- enum EnableResult { Sent, Deferred, Failed }
- class ReplaySnapshot: public ReplayState State; public bool OverlayReady; public string OverlayIdentity, Detail, HotkeyDisplay;
- interface IReplayPlatform: ReplaySnapshot Read(); EnableResult TryEnable(out string detail);
- class MonitorReport: public ReplaySnapshot Snapshot; public string Message; public int RecoveryCount, AttemptCount; public bool Paused; public string Phase;
- class ReplayMonitor: ctor(IReplayPlatform platform, Action<string> log); MonitorReport Tick(long nowMilliseconds, bool paused); void Reset(long nowMilliseconds);
- class NvidiaPlatform: IReplayPlatform; 无参数构造函数。
- static class AppFiles: public static string DataDirectory; public static void Log(string message); public static void WriteJson(string filename, object value);

Tick 使用 Stopwatch.ElapsedMilliseconds。默认3秒检测，启动20秒宽限、叠加层变化后10秒就绪等待、连续两次关闭才操作、尝试后9秒确认，失败按30/60/120/300秒间隔重试。未知状态和缺失快捷键不操作，发送前即时复读状态。

2026-10-20：Tick 必须显式传入 gameRunning。无游戏时在平台读取前返回 waiting-game，并清除待恢复状态；重新进入游戏重新宽限。GameActivityDetector 根据当前会话进程完整路径匹配 Windows GameConfigStore 和 games.txt（支持 ! 排除），目录/配置约15秒刷新，配置异常时停止检测。NvidiaPlatform 在 SendInput 前调用游戏复查委托，取消游戏已退出时的恢复。托盘提供添加/编辑游戏列表，非游戏状态不显示旧回放读数。

InstallationPaths 为运行中的进程、快捷方式与安装脚本提供统一的物理文件路径比较，兼容 Windows 包装应用对 LocalAppData 的路径虚拟化；安装与卸载脚本通过 tools/installation-paths.ps1 复用该源码。

## UI 与生命周期（UI代理负责 src/Program.cs、src/TrayApplication.cs、src/Startup.cs）
- [STAThread] main，无窗口启动，ApplicationContext 托盘，status.json 输出 MonitorReport（匿名对象或字典均可）。
- 菜单：实时状态（disabled）、暂停/恢复守护、开机自启动勾选项、查看状态窗口、打开日志文件夹、退出。
- 小型中文状态窗口，关闭隐藏，显示开启/关闭/未知、暂停、恢复次数、上次检查、自启动、状态原因和快捷键。
- Program 参数：--diagnose（Read，只写 diagnostics.json，不开启托盘）、--enable-startup、--disable-startup、--exit。
- 当前用户单实例 Mutex，以及命名 ManualResetEvent 供 --exit 通知运行程序。重复启动不出现窗口。命名含当前用户SID，Local作用域。
- Startup: public static bool IsEnabled(); public static void SetEnabled(bool value); 当前用户 Startup 文件夹内的 ReplayKeeper.lnk 指向当前 exe，通过 WScript.Shell 创建与读回，先写临时链接再替换；只移除目标匹配的链接和旧版 HKCU Run 值。用户手动打开 exe 只启动，不默默开启自启动。安装脚本负责默认启用，失败回滚恢复旧链接的字节与原 Run 值。
- 所有 UI 更新与 Timer 在同一 UI 线程。SystemEvents SessionSwitch/PowerModeChanged 只标记，通过下一 tick Reset 给恢复宽限；锁定时暂停恢复但保留用户暂停意愿。
- 不退出 NVIDIA、不改变录制设置、不修改全局快捷键。

## 构建 / 安装（安装代理负责 build.ps1、install.ps1、uninstall.ps1、app.manifest、README.md）
- build.ps1 编译所有 src/*.cs 为 dist/ReplayKeeper.exe；引用 System.Windows.Forms,System.Drawing,System.Web.Extensions；UTF-8中文源码；x64 WinExe，embed app.manifest。
- 安装编译后复制exe到 %LOCALAPPDATA%/ReplayKeeper/ReplayKeeper.exe，先通过 --exit 等待自身退出，调用 --enable-startup，Start-Process -WindowStyle Hidden 启动。
- 卸载先 --exit，--disable-startup，只删除指定安装exe（保留日志）；保护路径，不递归删除。
- .NET Framework 4.7.2+ Windows 10/11。No admin。
- 说明首次守护、托盘暂停和退出行为、开机自启其实是用户登录、3秒轮询与恢复等待、受保护内容/存储/NVIDIA更新限制、状态字段非官方API，未验证重启时不要写已验证。
- 研究出处：https://github.com/Verpous/AlwaysShadow/blob/master/src/fixer.c 以及 https://www.nvidia.cn/geforce/news/nvidia-app-beta-update-av1-performance-tuning/。

## 验证（根代理负责 tests）
- 状态机：已开启不发送、未知不发送、初始化等待、两次关闭才发送、复查成功、失败退避、暂停、叠加层变化、恢复等待。
- 注册表读取 REG_BINARY/DWORD、缺失/非法字段、配置快捷键；测试独立临时HKCU子树，不改 NVIDIA键。
- 本机只读状态诊断、安装、自启动项、单实例、托盘退出与重启。
- 当前回放开启，不能为测试清空用户回放缓冲；真实关闭→恢复测试需等待自然关闭或用户自行关闭。
