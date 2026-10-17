# QoderMonitor —— Qoder CN 会话监控

最初是在用 DeepSeek Harness（DSH）时，觉得它显示的输出速度、缓存命中等数据很有意思，能一眼看清模型这段时间的具体使用情况。Qoder CN 的界面上恰好有一块空白位置，于是照着这个思路做了这个小工具：把同样的会话指标放进一条不遮挡内容的透明窄条里。

它只读 Qoder CN 写在本地磁盘的会话记录，算出模型用时、工具用时、轮数/步数、tok/s、上下文、缓存命中与 credits，显示为一条默认置顶的透明窄条，也可作为 Qoder CN 的 statusLine。

![QoderMonitor 悬浮条示例](docs/preview.png)

上图为示例数据；窄条背景本身完全透明，图中浅色底只是截图时临时铺的衬底。

![嵌入 Qoder CN 状态栏](docs/qoder-cn-status.png)

第二张为 Qoder CN 状态栏中的实际显示效果，两行为示例会话数据。

## 快速开始

**系统要求**：Windows 10/11 x64、已安装 Qoder CN、Python 3（采集端只用标准库）；不需要管理员权限。

1. 从 [Releases](https://github.com/StephenBoyMi/QoderMonitor/releases/latest) 下载最新的 zip，解压到任意目录。
2. 双击 `安装到桌面.vbs`，它按当前目录创建桌面快捷方式。
3. 双击桌面的「Qoder 悬浮监控」开关；绿卡表示已开启，灰卡表示已关闭，红卡表示切换失败并给出原因。

`bar_dcomp.exe` 未做代码签名，Windows SmartScreen 可能提示“未知发布者”，选择“更多信息 → 仍要运行”即可。

## 组成

| 文件 | 说明 |
| --- | --- |
| `qoder_monitor.py` | 数据层，纯标准库、无界面。`SessionStats` 增量解析会话，`metrics()` 汇总指标，`decode_tps()` 回归估算输出速度 |
| `bar_dcomp.exe` | 渲染层，唯一显示端；DirectComposition + Direct2D/DirectWrite，逐像素 alpha |
| `dcomp\App.cs` | 渲染端主体：窗口、布局、绘制、拖动、菜单、前台跟随、单实例 |
| `dcomp\Interop.cs` | Win32 / D3D / DXGI / DComp / D2D / DWrite 的 P/Invoke、vtable 槽位与 GUID |
| `monitor-toggle.ps1` | 开关：判运行状态、启停渲染端与采集端、换桌面图标、弹状态卡片 |
| `toggle-monitor-hidden.vbs` | 开关的无控制台启动器，桌面快捷方式指向它 |
| `启动悬浮窗.vbs` | 只启动、不弹卡片的入口，适合开机自启 |
| `install-shortcut.ps1` | 按脚本所在目录重建桌面快捷方式（由 `安装到桌面.vbs` 调用） |
| `set-lnk-icon.ps1` | 在彩色/灰色两套图标间切换 |
| `icons\` | `monitor-on`、`monitor-off` 两套 ico/png 与生成脚本 |
| `docs\` | README 中的两张示例截图 |
| `LICENSE` | MIT 许可证 |
| `README.md` | 本文档 |

## 数据流

```
~/.qoder-cn/projects/*/*.jsonl
        │  SessionStats 增量解析
        ▼
qoder_monitor.py --emit ──每秒原子写──▶ bar_state.json
                                              │
                                              ▼
                                   bar_dcomp.exe 读文件并绘制
```

- `--emit` 常驻采集，先写 `.tmp` 再 `os.replace`，渲染端不会读到半截 JSON。
- 渲染端启动时自行拉起 `--emit`，退出时带走；单实例由 `Global\QoderMonitorBar` 互斥体保证。
- 开关通过 `Global\QoderMonitorToggle` 串行化，连点过快会提示“上一次点击还没结束”。

## 使用

**安装/换路径**：把整个文件夹放到任意位置，双击 `安装到桌面.vbs` 重建快捷方式；之后双击桌面“Qoder 悬浮监控”即可开关窄条。

**静默启动**：双击 `启动悬浮窗.vbs`，或在命令行运行 `bar_dcomp.exe --always`（忽略前台跟随，便于调试）。

**停止**：双击桌面图标、双击窄条、右键菜单“退出”，或结束 `bar_dcomp.exe` 与 `qoder_monitor.py --emit` 的 `pythonw.exe`。

**开机自启**：给 `启动悬浮窗.vbs` 建快捷方式，放进 `shell:startup`。

**可移植性**：脚本路径全部由自身目录推出；`pythonw` 依次按 `QODER_MONITOR_PYTHON`、常见安装目录、`%LOCALAPPDATA%\Programs\Python`、PATH 查找；数据目录按 `QODER_MONITOR_HOME`、`~/.qoder-cn`、`~/.qoder` 探测。如果使用 statusLine，唯一需要手改的是 `settings.json` 里的那条命令。

## 界面与操作

- 一条无底板的透明窄条，最多两个会话行；每行：状态点、标题、轮数/步数、tok/s、总 tokens 与缓存命中、上下文环、credits。
- 取最近 30 分钟内有写入的会话，按最后修改时间由新到旧最多显示两行；每行的数字是该会话自己的整场累计。
- 状态点绿色表示会话忙或文件 25 秒内有写入，灰色为空闲；标题超过 150 DIP 截断，两行共用列宽。
- 左键按在字或图标上拖动，松手把位置写入 `pos.json`；右键菜单可退出、切换置顶、切换“只在 Qoder 前台时显示”；双击退出。
- 前台跟随两道闸：前台进程名以 `qoder` 开头且不以 `ide.exe` 结尾，并且聊天端存在可见主窗口；任一不满足就隐去。退出 Qoder 只是隐藏，不结束采集。
- 零会话或 `bar_state.json` 超过 15 秒未更新时整条隐去，有数据自动恢复。

## 指标口径

| 指标 | 来源与说明 |
| --- | --- |
| 模型用时 | 请求发出到响应落盘的时间戳配对；与 `qodercli.log` 交叉核对差 0.9% |
| 工具用时 | 模型回复结束到最后一个 `tool_result` 写回；并行工具合并为一段 |
| 轮数 / 步数 | 轮 = 不同 `promptId`，会低估；步 = 已闭合的 assistant 响应组数，以步数为准 |
| tok/s | 优先用逐请求「时长—输出 token」回归得到的边际解码速率，尽量剥离排队与首字等待；样本不足或拟合差时退回端到端平均并标 `(e2e)`，拟合样本含字符估算时标 `(估)`。它是估计值，不保证与 dsh 逐位一致 |
| 上下文 % | 优先 Qoder 回传的 `context_usage_ratio`；窗口大小取 Qoder 回传值（本机为 1,000,000） |
| 缓存命中 | `cache_read ÷ input_tokens`；Qoder 的 `input_tokens` 已包含 cache_read，代码另有 `cache_read > input` 的护栏 |
| credits | 优先 Qoder 回传的 `total_credits`；回退到 transcript 时按 `request_id` 去重累加。各行独立，不做跨会话合计 |
| TTFT | 客户端不记录，界面不提供 |

系统模型默认把 token 抹零，只有 BYOK 或设置环境变量 `QODERCN_EXPOSE_TOKEN_USAGE=1` 并重启 Qoder CN 后才保留真值；没有真值时按字符估算（中日韩 1.4 字/token，其余 4 字符/token），误差约 10~20%。

## statusLine（可选）

`~/.qoder-cn/settings.json` 顶层：

```json
"statusLine": {
  "type": "command",
  "command": "<python.exe 的绝对路径> <QoderMonitor 目录>\\qoder_monitor.py --statusline"
}
```

把两个占位符换成自己的路径；该文件还可能同时存有 hooks 和 providers（其中可能含明文 apiKey），修改时必须“备份 → 只加这一个键 → 逐键比对”，不要整份覆盖。`--statusline` 优先采信 Qoder 从 stdin 传来的上下文与 credits，其余从 transcript 推算。设置项 `requiresRestart: false`，但正在运行的会话不会重读设置，新开一个会话才会出现这一行。

## 重新编译

先退出正在运行的 `bar_dcomp.exe`，再在 `dcomp\` 目录下：

```
csc -nologo -target:winexe -out:..\bar_dcomp.exe -r:System.Web.Extensions.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll Interop.cs App.cs
```

`csc.exe` 用系统自带的 .NET Framework 版本即可，通常在 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，不必安装 Visual Studio。

运行时实测：两行示例数据 850×57 物理像素，私有内存 65 MB、工作集约 80 MB，常显时 20 秒 CPU 约 0.03 秒。

## 运行时文件

| 文件 | 内容 |
| --- | --- |
| `bar_state.json` | 采集端每秒改写的统计：`sessions[]`、`extra`、`ts` |
| `pos.json` | `dcomp_x` / `dcomp_y`（DIP）、`pin`、`follow` |
| `bar_pid.json` | 渲染端与采集端 PID，供开关在 CIM 查询失败时兜底 |
| `logs\bar_dcomp.log` | 启动 DPI、surface 尺寸、SHOW/HIDE、异常 |

## 验证

```
python qoder_monitor.py --check
```

正常输出包含 `usage_mode`、`坏行 = 0` 与一行汇总；启动窄条后 `logs\bar_dcomp.log` 应出现 `dpi=...`、`surface ...x...`、`SHOW fg=Qoder CN.exe`。

## 许可证

本项目以 MIT 许可证发布，详见 [LICENSE](LICENSE)。

## 鸣谢

[TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor) 的作者 [zhongyang219](https://github.com/zhongyang219)：这个项目的轻量悬浮显示与 Windows 窗口处理思路给了本项目重要参考，特此致谢。本项目未直接复用其代码。
