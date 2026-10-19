# C# 单 exe 原型

状态：数据层与渲染端已合并，`single/QoderMonitor.exe` 可以独立运行。双击即显示悬浮条，进程内读取 transcript，不再启动 Python，也不读写 `bar_state.json`。这是 `single-exe` 分支的实验版本，仓库主线仍保留 Python + `bar_dcomp.exe` 的稳定版本。本机桌面快捷方式已切到单 exe；旧版的 `安装到桌面.vbs` 现在会优先安装单 exe。

## 构建

```powershell
powershell -File build-app.ps1   # 单 exe 原型 -> QoderMonitor.exe
powershell -File build.ps1       # 数据层验证程序 -> qoder_data.exe
```

均为 .NET Framework 4.8 + 系统自带 `csc.exe`，不需要 Visual Studio。

## 已实现

- `Data.cs`：路径探测、JSONL 增量解析、`SessionStats`、`decode_tps`、`metrics`、标题读取、会话发现
- `App.cs`：DirectComposition 透明窄条，数据来自进程内 `SessionCache`
- 单实例：第二次启动会唤醒已有窗口，不会叠出第二条
- 托盘图标：显示悬浮条、总在最前、只在 Qoder 前台时显示、开机自启、退出
- 手动启动后有 8 秒强制显示窗口，避免从资源管理器启动时被前台跟随立刻隐去
- 无活跃会话时只保留托盘图标，不显示空窄条

实测两行窄条约 92 MB 工作集，启动后稳定，不再随运行时间上涨。

## 验证

```powershell
python parity_test.py
```

冻结 8 个 transcript，对同一份数据比较 Python/C# 的快照、标题、statusline 和 JSON；另用全部静态 transcript 做快照比对，当前 63/63 通过。

## 未做

- 无会话时的托盘气泡提示
- 代码签名，首次运行仍可能触发 SmartScreen
- 发布包与 README 主文档尚未切到单 exe

## 接口

| 命令 | 作用 |
| --- | --- |
| `QoderMonitor.exe` | 启动单 exe 悬浮条，托盘常驻 |
| `QoderMonitor.exe --autostart` | 开机自启入口，不强制显示 |
| `QoderMonitor.exe --always` | 忽略前台跟随，一直显示 |
| `qoder_data.exe --check/--json/--snapshot/--title/--statusline/--emit` | 数据层验证入口 |
