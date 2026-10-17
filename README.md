# QoderMonitor —— Qoder CN 会话监控

只读 Qoder CN 写在本地磁盘的会话记录，算出模型用时、工具用时、轮步、tok/s、上下文、缓存命中与 credits，显示为一条置顶的透明窄条，也可作为 Qoder CN 的 statusLine。

![QoderMonitor 悬浮条示例](docs/preview.png)

上图为示例数据；窄条背景本身完全透明，图中浅色底只是截图时临时铺的衬底。

## 组成

| 文件 | 说明 |
| --- | --- |
| `qoder_monitor.py` | 数据层，纯标准库、无界面。`SessionStats` 增量解析会话，`metrics()` 汇总指标，`decode_tps()` 估算输出速度 |
| `bar_dcomp.exe` | 渲染层，唯一显示端；DirectComposition + Direct2D/DirectWrite，逐像素 alpha |
| `dcomp\App.cs` | 渲染端主体：窗口、布局、绘制、拖动、菜单、前台跟随、单实例 |
| `dcomp\Interop.cs` | Win32 / D3D / DXGI / DComp / D2D / DWrite 的 P/Invoke、vtable 槽位与 GUID |
| `monitor-toggle.ps1` | 开关：判运行状态、启停渲染端与采集端、换桌面图标、弹状态卡片 |
| `toggle-monitor-hidden.vbs` | 开关的无控制台启动器，桌面快捷方式指向它 |
| `启动悬浮窗.vbs` | 只启动、不弹卡片的入口，适合开机自启 |
| `install-shortcut.ps1` | 按脚本所在目录重建桌面快捷方式（由 `安装到桌面.vbs` 调用） |
| `set-lnk-icon.ps1` | 在彩色/灰色两套图标间切换 |
| `icons\` | `monitor-on`、`monitor-off` 两套 ico/png 与生成脚本 |
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

**可移植性**：脚本路径全部由自身目录推出；`pythonw` 依次按 `QODER_MONITOR_PYTHON`、常见安装目录、`%LOCALAPPDATA%\Programs\Python`、PATH 查找；数据目录按 `QODER_MONITOR_HOME`、`~/.qoder-cn`、`~/.qoder` 探测。唯一需要手改的是 `settings.json` 里的 statusLine 命令。

## 界面与操作

- 一条无底板的透明窄条，最多两个会话行；每行：状态点、标题、轮步、tok/s、总 tokens 与缓存命中、上下文环、credits。
- 状态点绿色表示会话忙或文件 25 秒内有写入，灰色为空闲；标题超过 150 DIP 截断，两行共用列宽。
- 左键按在字或图标上拖动，松手把位置写入 `pos.json`；右键菜单可退出、切换置顶、切换“只在 Qoder 前台时显示”；双击退出。
- 前台跟随两道闸：前台进程名以 `qoder` 开头且不是 `Qoder CN IDE.exe`，并且聊天端存在可见主窗口；任一不满足就隐去。退出 Qoder 只是隐藏，不结束采集。
- 零会话或 `bar_state.json` 超过 15 秒未更新时整条隐去，有数据自动恢复。

## 指标口径

| 指标 | 来源与说明 |
| --- | --- |
| 模型用时 | 请求发出到响应落盘的时间戳配对；与 `qodercli.log` 交叉核对差 0.9% |
| 工具用时 | 模型回复结束到最后一个 `tool_result` 写回；并行工具合并为一段 |
| 轮数 / 步数 | 轮 = 不同 `promptId`，会低估；步 = 已闭合的 assistant 响应组数，以步数为准 |
| tok/s | 逐请求生成段回归估算；纯估算时数字后标 `估`；含排队与首字等待，是端到端口径，不等同于 dsh 的 TTFT 外口径 |
| 上下文 % | 优先 Qoder 回传的 `context_usage_ratio`，窗口 1,000,000 |
| 缓存命中 | `cache_read ÷ input_tokens`；Qoder 的 `input_tokens` 已包含 cache_read，代码另有 `cache_read > input` 的护栏 |
| credits | Qoder 回传值，按 `request_id` 去重累加；各行独立，不做跨会话合计 |
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

把两个占位符换成自己的路径；该文件同时存有 hooks 和含明文 apiKey 的 providers，修改时必须“备份 → 只加这一个键 → 逐键比对”，不要整份覆盖。`--statusline` 优先采信 Qoder 从 stdin 传来的上下文与 credits，其余从 transcript 推算。

## 重新编译

先退出正在运行的 `bar_dcomp.exe`，再在 `dcomp\` 目录下：

```
csc -nologo -target:winexe -out:..\bar_dcomp.exe -r:System.Web.Extensions.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll Interop.cs App.cs
```

运行时实测：窗口两行 850×57 物理像素，私有内存 65 MB、工作集约 80 MB，常显时 20 秒 CPU 约 0.03 秒。

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
