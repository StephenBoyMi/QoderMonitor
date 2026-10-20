# C# 单 exe 原型

状态：数据层与渲染端已合并，`single/QoderMonitor.exe` 是 2.0 起的单文件发布版。双击即显示悬浮条，进程内读取 transcript，不再启动 Python，也不读写 `bar_state.json`。仓库里仍保留 Python + `bar_dcomp.exe` 的 1.0.x 稳定版作回退。

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
- 配置和日志放在 `%LOCALAPPDATA%\QoderMonitor\`，exe 本身可以单独放在桌面，不会在桌面生成 `pos.json` 或 `logs`

实测两行窄条约 92 MB 工作集，启动后稳定，不再随运行时间上涨。

## 验证

```powershell
python parity_test.py                     # 冻结 8 个 transcript，逐项比较 Python/C#
python ..\tests\test_switch_window.py     # 换模型场景：证明旧模型样本不再稀释 tok/s
python ..\tests\test_robust_slope.py      # 稳健斜率：一条慢请求拉不动显示的 tok/s
python ..\tests\test_tool_time.py          # 工具用时：等人 3 小时不计入工具/模型用时
python ..\tests\sweep_static_parity.py    # 全部静态 transcript 的快照比对（当前 66/66）
```

`parity_test.py` 冻结 8 个 transcript，对同一份数据比较 Python/C# 的快照、标题、statusline 和 JSON。

tok/s 的口径是「活跃窗 + 同模型」：以最后一条请求的结束时刻为锚点向前 10 分钟，再只保留与当前模型同名的样本；回归不成立时在同一集合上退回端到端并标 `(e2e)`。`tests/test_switch_window.py` 每次运行会生成 `tests/switch_dilution.jsonl`（10 条快样本 + 2 条慢样本落在同一个窗内），旧版判据下该文件报 144 tok/s、新版报 12 tok/s。

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
| `QoderMonitor.exe --statusline` | stdin 读 Qoder payload，stdout 输出一行 |
| `QoderMonitor.exe --check` | 输出一行关键指标，供诊断 |
| `qoder_data.exe --check/--json/--snapshot/--title/--statusline/--emit` | 数据层验证入口 |
