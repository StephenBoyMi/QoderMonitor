# C# 数据层（单 exe 方案第一步）

状态：数据层已从 `qoder_monitor.py` 移植完成，尚未接入渲染端。现有可运行版本仍然使用 Python 采集端 + `bar_dcomp.exe`，本目录是独立实验分支的代码。

## 构建

系统自带 .NET Framework 的 `csc.exe` 即可，不需要 Visual Studio：

```powershell
powershell -File build.ps1
```

等价命令：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:exe -out:qoder_data.exe -r:System.Web.Extensions.dll -r:System.Core.dll Data.cs Program.cs
```

## 验证

```powershell
python parity_test.py
```

会冻结 8 个 transcript，对同一份数据分别运行 Python 与 C#，逐项比较快照、标题、statusline 和 JSON。当前结果：全部通过。另用全部 64 个静态 transcript 做过快照比对，64/64 通过。

## 接口

| 命令 | 作用 |
| --- | --- |
| `qoder_data.exe --check` | 打印当前最活跃会话，格式与 Python 版对应 |
| `qoder_data.exe --json` | 输出前两个活跃会话的 JSON |
| `qoder_data.exe --snapshot <file.jsonl>` | 单个 transcript 的完整快照，parity 用 |
| `qoder_data.exe --title <session_id>` | 从 `main.sqlite` 查标题 |
| `qoder_data.exe --statusline` | 从 stdin 读 Qoder payload，输出一行 |
| `qoder_data.exe --emit <file.json>` | 常驻采集，供旧渲染端联调 |

## 设计要点

- 标题读取用 Windows 自带的 `winsqlite3.dll`，不引入第三方 SQLite。
- JSON 用 `System.Web.Extensions`；`tool_use.input` 的字符估算按 Python `json.dumps` 的格式复刻。
- `SessionStats` 按增量读取 transcript；`Sessions` 用 `SessionCache` 保存已解析实例。
- 估算 token 时按 Unicode 码点计数，代理对按 1 个字符处理，和 Python 一致。
- 数据层不依赖 Python、不启动子进程，也不读写 `bar_state.json`。

## 下一步

把渲染端的 `ReadState()` 换成进程内 `SessionCache`，去掉 `--emit` 与 `bar_state.json`，再加入托盘图标、手动启动强制显示和无会话占位。完成后才是真正的单 exe。
