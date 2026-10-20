"""
Qoder CN 会话监控悬浮窗
=======================

它做什么
    读 Qoder CN 自己写在本地磁盘的会话记录，实时算出：模型用时、工具用时、
    轮数/步数、输出速度、上下文占用、credits 消耗，写成 bar_state.json 供渲染端显示。

它不做什么
    不改动 Qoder CN 本体（不碰 app.asar），不联网，不注入任何进程。
    全部数据来自只读文件，删掉本目录即可完全回退。

数据源（本机实测确认，2026-10-15）
    ~/.qoder-cn/projects/<项目id>/<会话id>.jsonl   唯一数据源，Claude Code 风格的完整对话记录
    （~/.qoder-cn/logs/runs/<run>/qodercli.log 只用于一次性交叉核对：
     同一会话它按 request_id 配对算出 37 请求 / 578.6 秒，本脚本从 transcript
     算出 37 步 / 584 秒，差 0.9%。本脚本运行时不读它。）

已知精度边界（2026-10-15 本机重算口径：145 个 transcript、16033 条带 usage 的
assistant 记录，其中 token 非零仅 411 条）
    * Qoder CN 的系统模型不回传 token：qfmodel 15174 条、qmodel_38max 284 条、
      fast 164 条，非零数全部为 0；而 6 个 BYOK 模型（qoder-custom-*）非零率 100%
      （213/213、189/189、4/4…）。
      原因在 CLI 代码里，不是服务端没给：worker runtime 的 bv(usage) 函数在开关
      关闭时强制 input_tokens/output_tokens/cache_* 写 0，只有 modelSource 属于
      BYOK 或环境变量放行时才保留真值。
      → 这类模型的 tok/s 只能按字符数估算，界面标 "(估)"。
    * 打开 QODERCN_EXPOSE_TOKEN_USAGE=1 并重启 Qoder CN，系统模型也会落真值，
      本脚本会自动切到实测模式（见 README）。
    * 首 token 延迟（TTFT）拿不到：Qoder CN 客户端不记录流式首块时间，
      全量日志里不存在 first_token / ttft 事件。
    * credits 与 context_usage_ratio 是 Qoder CN 真实回传值，可直接采信。
"""

from __future__ import annotations

import glob
import json
import os
import sys
import time
from datetime import datetime, timezone

HOME = os.path.expanduser("~")


def _qoder_home():
    """数据目录自动探测，为了可移植：换机器、换用户、装的是国际版都不用手改。

    优先级：环境变量 QODER_MONITOR_HOME → ~/.qoder-cn（CN 版，本机实测）→
    ~/.qoder（国际版目录名）。都探不到就仍返回 ~/.qoder-cn，让读端报
    "读不到状态文件"而不是悄悄去读错地方。
    """
    env = os.environ.get("QODER_MONITOR_HOME", "").strip()
    if env and os.path.isdir(env):
        return env
    for name in (".qoder-cn", ".qoder"):
        cand = os.path.join(HOME, name)
        if os.path.isdir(os.path.join(cand, "projects")):
            return cand
    return os.path.join(HOME, ".qoder-cn")


QODER_HOME = _qoder_home()
PROJECTS_DIR = os.path.join(QODER_HOME, "projects")

# 活跃判定：会话文件最后修改在此秒数内，认为 Qoder 正在跑
ACTIVE_WINDOW_S = 25.0
MAX_ROWS = 2          # 窄条最多显示两个会话行


# ---------------------------------------------------------------- 工具函数

def parse_ts(v):
    """把 transcript 里的 timestamp 转成 epoch 秒。

    实测：assistant / user 记录是 ISO 字符串（带 Z 或 +08:00），
    active-leaf / runtime-config 是整数 epoch 毫秒，
    file-history-snapshot / workspace-directories 是 None。
    只有 ISO 字符串参与时间计算，其余返回 None。
    """
    if not isinstance(v, str):
        return None
    try:
        s = v.strip()
        if s.endswith("Z"):
            s = s[:-1] + "+00:00"
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.timestamp()
    except Exception:
        return None


def is_cjk(ch: str) -> bool:
    """是否属于中日韩文字或全角标点（这些用宋体渲染）。"""
    o = ord(ch)
    return (
        0x4E00 <= o <= 0x9FFF
        or 0x3400 <= o <= 0x4DBF
        or 0x3040 <= o <= 0x30FF
        or 0xAC00 <= o <= 0xD7AF
        or 0x3000 <= o <= 0x303F
        or 0xFF00 <= o <= 0xFFEF
    )


def split_runs(s: str):
    """把字符串切成 [(片段, 是否中日韩), ...]，用于分别指定宋体与 Times New Roman。

    Tk 的一个 Label 只能设一个字体族，交给系统回退不保证中文落到宋体，
    所以显式分段，由调用方每段建一个控件。
    """
    runs = []
    buf = []
    cur = None
    for ch in s:
        k = is_cjk(ch)
        if cur is None or k == cur:
            buf.append(ch)
            cur = k
        else:
            runs.append(("".join(buf), cur))
            buf = [ch]
            cur = k
    if buf:
        runs.append(("".join(buf), cur))
    return runs


def estimate_tokens(text: str) -> float:
    """按字符数估算 token，用于 Qoder 系统模型不回传 output_tokens 的情况。

    这是经验近似，不是 Qoder 官方口径：
      * 中日韩字符（含全角标点）按 1 token ≈ 1.4 字折算
      * 其余（拉丁字母、数字、符号、空格、JSON 结构符）按 1 token ≈ 4 字符折算
    实测误差量级约 10~20%，所以界面上必须标 "(估)"，不能当账用。
    """
    if not text:
        return 0.0
    cjk = sum(1 for ch in text if is_cjk(ch))
    other = len(text) - cjk
    return cjk / 1.4 + other / 4.0


def blocks_of(message):
    """取 message.content 并保证返回列表。"""
    c = (message or {}).get("content")
    if isinstance(c, list):
        return c
    if isinstance(c, str):
        return [{"type": "text", "text": c}]
    return []


def fmt_dur(sec: float) -> str:
    """把秒数排成紧凑时长。"""
    if sec is None or sec < 0:
        sec = 0.0
    if sec < 60:
        return f"{sec:.0f}秒"
    m, s = divmod(int(round(sec)), 60)
    if m < 60:
        return f"{m}分{s:02d}秒"
    h, m = divmod(m, 60)
    return f"{h}时{m:02d}分"


# ---------------------------------------------------------------- 会话标题

# Qoder CN 界面左侧那些可读标题不在 CLI 的 transcript 里（实测该文件没有任何
# 标题字段），而是存在 Electron 的 userData 数据库中：
#   %APPDATA%\com.qodercn.app.stable\main.sqlite  表 chat_sessions
#   列 session_id（就是 transcript 文件名去掉 .jsonl）+ title + extra_json
TITLE_DB = os.path.join(
    os.environ.get("APPDATA", os.path.join(HOME, "AppData", "Roaming")),
    "com.qodercn.app.stable", "main.sqlite")
TITLE_TTL = 5.0
_title_cache = {}


def session_title(sid, fallback=""):
    """按 session_id 查会话标题，查不到回退到 fallback（通常是 UUID 前 4 位）。

    必须用 mode=ro 打开：这个库是 WAL 模式且 Qoder 正在写它。实测
    immutable=1 会绕过 WAL 读不到最新标题，nolock=1 直接报
    unable to open database file。

    标题本身会变：新建会话时先落 extra_json.provisionalTitle（首条提问截断），
    首轮结束后由 AI 重命名，用户还能手改覆盖。所以这里只缓存 5 秒。
    """
    if not sid:
        return fallback
    now = time.time()
    hit = _title_cache.get(sid)
    if hit and now - hit[1] < TITLE_TTL:
        return hit[0]
    title = None
    try:
        import sqlite3
        if os.path.exists(TITLE_DB):
            con = sqlite3.connect(f"file:{TITLE_DB}?mode=ro", uri=True, timeout=1.0)
            try:
                row = con.execute(
                    "SELECT title, extra_json FROM chat_sessions WHERE session_id=?",
                    (sid,)).fetchone()
            finally:
                con.close()
            if row:
                title = (row[0] or "").strip()
                if not title and row[1]:
                    try:
                        title = (json.loads(row[1]).get("provisionalTitle") or "").strip()
                    except Exception:
                        title = ""
    except Exception:
        title = None            # 抢锁或库缺失：不写缓存，下一轮重试
    if not title:
        return hit[0] if hit else fallback
    if len(title) > 14:
        title = title[:13] + "…"     # … 在 GBK 里有，不会踩编码坑
    _title_cache[sid] = (title, now)
    return title


def fmt_tokens(n) -> str:
    """压缩大数，对齐 dsh 底部那条的写法：4700000 → 4.7M。"""
    if n is None:
        return "—"
    n = float(n)
    if n >= 1e6:
        return f"{n/1e6:.1f}M"
    if n >= 1e3:
        return f"{n/1e3:.1f}k"
    return f"{n:.0f}"


# ---------------------------------------------------------------- 状态机

class SessionStats:
    """增量解析一个 transcript 文件。

    只读新增字节，把每行喂进状态机，累计值因此始终等于整场会话的真实值，
    不会因为"只看最后 N 行"而少算 credits。
    """

    def __init__(self, path: str):
        self.path = path
        self.offset = 0
        # 累计量
        self.model_s = 0.0
        self.tool_s = 0.0
        self.credits = 0.0
        self.steps = 0            # 已完成的模型请求数
        self.turn_ids = set()     # 不同 promptId → 用户轮数
        self.err_steps = 0
        self.out_tokens_true = 0.0
        self.out_tokens_est = 0.0
        self.in_tokens_true = 0.0
        self.cache_read_true = 0.0
        self.has_true_usage = False
        # 速度必须按请求配对：真值窗口与估算窗口各自累加自己的分子和分母。
        # 早先版本用「整场会话只要出现过一条非零就整体按实测」的判据，
        # 在跨重启的会话里会算出 8 tok/s 这种荒谬值——分子只含重启后的 token，
        # 分母却是重启前后的全部时长。
        self.meas_tok = 0.0
        self.meas_dur = 0.0
        self.est_tok = 0.0
        self.est_dur = 0.0
        # 逐请求的 (输出token, 时长, 是否真值, 结束时刻, 模型名)，用于回归算边际解码速率。
        # 结束时刻与模型名是"活跃窗 + 同模型"两条过滤的依据：换模型后旧请求不该再进池。
        self.samples = []
        # 活跃窗长度与回归最少样本数，与 C# 版 Data.cs 的常量同值
        self.ACTIVE_WINDOW_S = 600.0
        self.DECODE_MIN_SAMPLES = 8
        self.ctx_ratio = None
        self.ctx_window = None
        self.model_key = None
        self.last_req_s = None    # 最近一次请求耗时（秒）
        self.last_req_tps = None  # 最近一次请求的输出速度
        self._seen_req = set()
        # 解析中间态
        self._cur = None          # 进行中的 assistant 响应组
        self._last_ts = None
        self._tool_window_start = None
        self._tool_window_end = None
        self.lines_bad = 0

    # ---- assistant 组生命周期

    def _open_group(self, ts):
        # 起点必须是"上一条记录的写入时刻"，也就是请求发出的时刻。
        # 实测同一次响应的 thinking/text/tool_use 三条 assistant 记录共用同一个
        # timestamp（响应完成瞬间批量落盘），若用本组自己的 ts 当起点，时长恒为 0。
        start = self._last_ts if self._last_ts is not None else ts
        self._cur = {
            "start": start,
            "end": ts,
            "cjk_est": 0.0,
            "usage": None,
            "req_id": None,
            "model": None,
        }

    def _close_group(self, next_ts):
        """把当前 assistant 组结算成模型用时 + 输出量。"""
        g = self._cur
        self._cur = None
        if g is None:
            return
        start, end = g["start"], g["end"]
        dur = None
        if start is not None and end is not None and end >= start:
            dur = end - start
            self.model_s += dur
        self.steps += 1

        u = g["usage"] or {}
        req_id = u.get("request_id") or g["req_id"]
        # credits 按 request_id 去重：实测一次请求的多条 assistant 记录里
        # 只有最后一条带 usage，但去重更保险。
        if req_id and req_id not in self._seen_req:
            self._seen_req.add(req_id)
            c = u.get("credits")
            if isinstance(c, (int, float)):
                self.credits += c

        dur0 = dur if dur is not None else 0.0
        ot = u.get("output_tokens")
        real = isinstance(ot, (int, float)) and ot > 0
        if real:
            self.has_true_usage = True
            self.out_tokens_true += ot
            self.meas_tok += float(ot)
            self.meas_dur += dur0
            it = u.get("input_tokens")
            if isinstance(it, (int, float)):
                self.in_tokens_true += it
            cr = u.get("cache_read_input_tokens")
            if isinstance(cr, (int, float)):
                self.cache_read_true += cr
            tok = float(ot)
        else:
            self.est_tok += g["cjk_est"]
            self.est_dur += dur0
            tok = g["cjk_est"]
        self.out_tokens_est += g["cjk_est"]

        ratio = u.get("context_usage_ratio")
        if isinstance(ratio, (int, float)) and ratio > 0:
            self.ctx_ratio = float(ratio)

        if dur and dur > 0.05 and tok > 0:
            self.last_req_s = dur
            self.last_req_tps = tok / dur
            self.samples.append((tok, dur, real, end, g["model"]))

        # 工具窗口从模型说完这一刻开始
        self._tool_window_start = end if end is not None else next_ts
        self._tool_window_end = None

    def _flush_tool_window(self):
        """结算一段工具用时。

        语义：从上一条模型响应结束，到最后一条 tool_result 写回之间的墙钟时间。
        并行工具调用会合并进同一段，因此它是"工具占用时间"而非"工具内部耗时"。
        只有真的出现过 tool_result 的窗口才结算：模型说完话、人隔几小时才开口的
        那段空闲既不是工具时间也不是模型时间，不能拿边界当终点计进去。
        """
        if self._tool_window_start is None:
            return
        if self._tool_window_end is not None and self._tool_window_end >= self._tool_window_start:
            self.tool_s += self._tool_window_end - self._tool_window_start
        self._tool_window_start = None
        self._tool_window_end = None

    # ---- 主入口

    def feed(self, o: dict):
        typ = o.get("type")
        ts = parse_ts(o.get("timestamp"))

        if typ == "runtime-config":
            cw = o.get("contextWindow")
            if isinstance(cw, (int, float)) and cw > 0:
                self.ctx_window = int(cw)
            if isinstance(o.get("model"), str):
                self.model_key = o["model"]
            return

        if typ == "assistant":
            m = o.get("message") or {}
            if self._cur is None:
                self._flush_tool_window()
                self._open_group(ts)
            if ts is not None:
                self._cur["end"] = ts
            for b in blocks_of(m):
                t = b.get("type")
                if t == "text":
                    self._cur["cjk_est"] += estimate_tokens(b.get("text") or "")
                elif t in ("thinking", "redacted_thinking"):
                    self._cur["cjk_est"] += estimate_tokens(b.get("thinking") or "")
                elif t == "tool_use":
                    try:
                        raw = json.dumps(b.get("input", {}), ensure_ascii=False)
                    except Exception:
                        raw = str(b.get("input", ""))
                    self._cur["cjk_est"] += estimate_tokens(raw)
            u = m.get("usage")
            if isinstance(u, dict):
                self._cur["usage"] = u
            if m.get("id"):
                self._cur["req_id"] = m.get("id")
            if isinstance(m.get("model"), str) and m["model"]:
                self._cur["model"] = m["model"]
            if ts is not None:
                self._last_ts = ts
            return

        # 非 assistant：先结算进行中的响应组
        if self._cur is not None:
            self._close_group(ts)

        if typ == "user":
            m = o.get("message") or {}
            is_tool_result = any(
                b.get("type") == "tool_result" for b in blocks_of(m)
            )
            pid = o.get("promptId")
            if is_tool_result:
                if ts is not None and self._tool_window_start is not None:
                    self._tool_window_end = max(self._tool_window_end or ts, ts)
            elif pid:
                # 真人提交的一轮，人的思考时间不计入任何用时
                self.turn_ids.add(pid)
                self._flush_tool_window()
            if o.get("model") and isinstance(o["model"], str):
                self.model_key = o["model"]

        if ts is not None:
            self._last_ts = ts

    def read_increment(self) -> bool:
        """读新增行，返回是否读到新内容。"""
        try:
            size = os.path.getsize(self.path)
        except OSError:
            return False
        if size < self.offset:      # 文件被换/截断
            self.__init__(self.path)
        if size == self.offset:
            return False
        got = False
        with open(self.path, "rb") as f:
            f.seek(self.offset)
            data = f.read()
            self.offset = f.tell()
        for line in data.split(b"\n"):
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line.decode("utf-8", "replace"))
            except Exception:
                self.lines_bad += 1
                continue
            if isinstance(o, dict):
                self.feed(o)
                got = True
        return got

    # ---- 对外快照

    def inflight(self, now=None):
        """正在生成、尚未落盘闭合的那一次请求。

        只读不写累计值——早先版本在这里顺手结算了组，结果一次响应被 thinking
        到达和 tool_use 到达拆成两次计数，steps 与 model_s 都会重复增长。
        """
        if self._cur is None or self._cur["start"] is None:
            return None
        now = now if now is not None else time.time()
        dur = max(0.0, now - self._cur["start"])
        u = self._cur["usage"] or {}
        ot = u.get("output_tokens")
        tok = float(ot) if isinstance(ot, (int, float)) and ot > 0 else self._cur["cjk_est"]
        return {
            "dur": dur,
            "tokens": tok,
            "tps": (tok / dur) if dur > 0.5 and tok > 0 else None,
            "thinking": self._cur["cjk_est"] > 0,
        }

    def decode_tps(self):
        """边际解码速率：请求时长对输出 token 做最小二乘，取斜率的倒数。

        斜率是「每多生成一个 token 多花多少秒」，倒数就是解码速率；截距才是排队、
        网络往返和首字等待这些与输出量无关的固定开销。

        与 dsh 的关系要说准：dsh 是逐请求精确扣掉 TTFT（decodeMs 从首个 token 算起，
        Σtokens/ΣdecodeMs）；Qoder 的 transcript 不落 chunk 时间戳，拿不到 firstToken，
        所以这里只能用回归估计同一个量。目标一致、估计方法不同，数值不会与 dsh 逐位相同。

        Qoder 不落 chunk 时间戳，拿不到真实 decode 窗口，回归是唯一可行的估计。
        样本太少或拟合太差时返回 None，让调用方退回端到端口径并如实标注，
        避免把噪声当性能报出去。
        """
        win = self.current_window()
        # 优先只用真值样本回归。跨重启的会话里早期那些请求的 token 是字符估算，
        # 混进来会让斜率（也就是解码速率）被估算误差污染；真值样本够用时宁可
        # 少用几个点，也不要把口径混成一锅。
        real = [(t, d) for t, d, mm, _e, _mod in win if mm]
        pts = [(t, d) for t, d, _mm, _e, _mod in win]
        if len(real) >= self.DECODE_MIN_SAMPLES:
            use, all_meas = real, True
        else:
            use, all_meas = pts, False
        n = len(use)
        if n < self.DECODE_MIN_SAMPLES:
            return None, n, False
        mx = sum(p[0] for p in use) / n
        my = sum(p[1] for p in use) / n
        sxx = sum((p[0] - mx) ** 2 for p in use)
        sxy = sum((p[0] - mx) * (p[1] - my) for p in use)
        syy = sum((p[1] - my) ** 2 for p in use)
        if sxx <= 0 or sxy <= 0 or syy <= 0:
            return None, n, False
        if sxy / (sxx * syy) ** 0.5 < 0.25:
            return None, n, False
        dec = 1.0 / (sxy / sxx)
        # 斜率接近 0 时 1/slope 会飞到几千 tok/s（dsh 自己的测试就记录过 26333 的假值），
        # 超出 1~500 不采信，退回端到端。
        if not (1.0 <= dec <= 500.0):
            return None, n, False
        return dec, n, all_meas

    def current_window(self):
        """活跃窗内的样本列表，每条是 (token, 时长, 是否真值, 结束时刻, 模型名)。

        锚点取窗内最后一条样本的结束时刻，往前 ACTIVE_WINDOW_S 秒。用样本时刻而不是
        墙钟，是为了让会话空闲时数字停在最后一次活跃期的值上，而不是掉成 —。
        再只保留"当前模型"（最后一条带模型名的样本）的样本：换模型后旧请求不该
        继续把斜率定在旧速度上。同模型样本不足时也照样只用同模型——宁可退回
        (e2e) 或 —，也不把别的模型混进同一个回归。
        """
        pts = [(t, d, mm, e, mod) for t, d, mm, e, mod in self.samples
               if t >= 20 and 0.3 <= d <= 600]
        if not pts:
            return pts
        anchor = max(e for _t, _d, _mm, e, _mod in pts)
        win = [row for row in pts if row[3] >= anchor - self.ACTIVE_WINDOW_S]
        cur = None
        for row in reversed(win):
            if row[4]:
                cur = row[4]
                break
        if cur is None:
            return win
        same = [row for row in win if row[4] == cur]
        return same if same else win

    def window_e2e(self):
        """活跃窗（同模型）的端到端速率：Σtokens/Σtime，含排队与首字。"""
        win = self.current_window()
        tok = sum(t for t, d, mm, e, mod in win if mm)
        dur = sum(d for t, d, mm, e, mod in win if mm)
        if dur > 0 and tok > 0:
            return tok / dur, True
        tok = sum(t for t, d, mm, e, mod in win if not mm)
        dur = sum(d for t, d, mm, e, mod in win if not mm)
        if dur > 0 and tok > 0:
            return tok / dur, False
        return None, False

    def snapshot(self):
        """--check 用的完整快照。数值字段直接取 metrics()，保证与窄条、
        statusline 同一口径；这里只追加诊断用的内部量。
        """
        m = metrics(self)
        m.update({
            "out_tokens": self.meas_tok + self.est_tok,
            "in_tokens": self.in_tokens_true,
            "cache_read": self.cache_read_true,
            "ctx_ratio": self.ctx_ratio,
            "model_key": self.model_key,
            "usage_mode": m["tps_kind"] or ("实测" if m["tps_measured"] else "估算"),
            "last_tps": self.last_req_tps,
            "meas_dur": self.meas_dur,
            "est_dur": self.est_dur,
            "samples": len(self.samples),
        })
        return m


# ---------------------------------------------------------------- 会话发现

def find_active_transcript():
    """返回 projects 下最近修改的会话文件。

    实测 transcript 路径形如 ~/.qoder-cn/projects/D--QoderCN/<uuid>.jsonl，
    一个会话一个文件，跨多次 run 连续追加，因此是天然的会话级统计单元。
    """
    best = None
    best_mt = 0.0
    for p in glob.glob(os.path.join(PROJECTS_DIR, "*", "*.jsonl")):
        try:
            mt = os.path.getmtime(p)
            sz = os.path.getsize(p)
        except OSError:
            continue
        if sz < 200:
            continue
        if mt > best_mt:
            best_mt, best = mt, p
    return best, best_mt


def find_active_transcripts(limit=3, recent_min=30.0):
    """返回最近有写入的若干会话 [(mtime, path), ...] 由新到旧，以及符合条件的总数。

    总数用来判断是否需要显示「其他 N 个会话」这一行。
    """
    now = time.time()
    rows = []
    for p in glob.glob(os.path.join(PROJECTS_DIR, "*", "*.jsonl")):
        try:
            mt = os.path.getmtime(p)
            sz = os.path.getsize(p)
        except OSError:
            continue
        if sz < 500:
            continue
        if (now - mt) > recent_min * 60.0:
            continue
        rows.append((mt, p))
    rows.sort(reverse=True)
    return rows[:limit], len(rows)


def foreground_is_qoder(mypid=None):
    """当前前台窗口是否属于 Qoder。返回 (是否, 进程名, 窗口标题)，取不到返回 None。

    判据以进程名为主（Qoder CN.exe、Qoder CN IDE 都含 qoder），标题为辅
    （有些窗口标题是文档名，不含应用名）。独立成模块级函数是为了能单独测，
    不用真的去切窗口才能验证判断对不对。
    """
    try:
        import ctypes
        from ctypes import wintypes
        u = ctypes.windll.user32
        k = ctypes.windll.kernel32
        hwnd = u.GetForegroundWindow()
        if not hwnd:
            return None
        pid = wintypes.DWORD()
        u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if mypid and pid.value == mypid:
            return None                      # 前台是窄条自己，让调用方保持原状
        name = ""
        h = k.OpenProcess(0x1000, False, pid.value)      # QUERY_LIMITED_INFORMATION
        if h:
            buf = ctypes.create_unicode_buffer(1024)
            size = wintypes.DWORD(1024)
            if k.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                name = buf.value
            k.CloseHandle(h)
        title = ""
        n = u.GetWindowTextLengthW(hwnd)
        if n:
            b2 = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(hwnd, b2, n + 1)
            title = b2.value
        low = (name + " " + title).lower()
        return ("qoder" in low, name, title)
    except Exception:
        return None


# ---------------------------------------------------------------- 一行文本


def metrics(st, payload=None):
    """统一取各项数值。build_line（自然语言）和 Overlay._row（定宽表格）共用，
    避免两处各算一遍导致口径漂移。

    取值优先级：Qoder 自己在 stdin 里算好的字段一律优先采信，只有它不给的
    （模型用时、工具用时、tok/s）才由 transcript 推算。
    """
    payload = payload if isinstance(payload, dict) else {}
    cw = payload.get("context_window") or {}
    cost = payload.get("cost") or {}

    used_pct = cw.get("used_percentage")
    ctx_window = cw.get("context_window_size")
    if used_pct is None and st is not None and st.ctx_ratio is not None:
        used_pct = st.ctx_ratio * 100.0
    if not ctx_window and st is not None:
        ctx_window = st.ctx_window

    credits = cost.get("total_credits")
    if credits is None and st is not None:
        credits = st.credits

    model = ((payload.get("model") or {}).get("display_name")
             or (payload.get("model") or {}).get("id")
             or (st.model_key if st else None) or "?")

    # 输出速度：优先报「边际解码速率」（回归斜率倒数，剥掉排队与首字等待），
    # 与 dsh 的 TPS 同口径。样本不足或拟合太差时退回端到端平均，并标 (e2e)
    # 说明口径已变，不假装它还是解码速度。
    tps, tps_measured, tps_kind = None, False, ""
    if st is not None:
        dec, _npts, all_meas = st.decode_tps()
        if dec is not None:
            tps, tps_measured = dec, all_meas
            tps_kind = "" if all_meas else "(估)"
        else:
            # 端到端回退同样只在活跃窗（同模型）的样本上算，不用整场累计的
            # meas_tok/meas_dur，否则换模型后数字仍被切换前的样本定住。
            e2e, meas = st.window_e2e()
            if e2e is not None:
                tps, tps_measured = e2e, meas
            tps_kind = "(e2e)"

    cache_pct = None
    if st is not None and st.in_tokens_true > 0 and st.cache_read_true > 0:
        # Qoder 的 input_tokens 含缓存部分（实测 context_usage_ratio == input/1e6），
        # 所以 cache_read/input 就是 dsh 的 cacheRead/(uncached+cacheRead) 口径。
        # 若字段改成互斥语义，比值会超过 100%，那就换分母。
        if st.cache_read_true > st.in_tokens_true:
            cache_pct = st.cache_read_true / (st.in_tokens_true + st.cache_read_true) * 100.0
        else:
            cache_pct = st.cache_read_true / st.in_tokens_true * 100.0

    # 总 token：与 dsh 底部那条同口径，是「各请求输入累加 + 输出累加」，
    # 所以输入里被缓存复用的历史会被重复计入，它反映的是消耗量不是会话体积。
    # Qoder 系统模型在开关生效前不落 input_tokens，那段时间无法计入，只能从
    # 有真值起累计，因此无真值时直接不显示，避免给出一个看着像总量的假数。
    total_tokens = None
    if st is not None and st.in_tokens_true > 0:
        total_tokens = st.in_tokens_true + st.meas_tok + st.est_tok

    return {
        "model_s": st.model_s if st else 0.0,
        "tool_s": st.tool_s if st else 0.0,
        "turns": len(st.turn_ids) if st else 0,
        "total_tokens": total_tokens,
        "steps": st.steps if st else 0,
        "tps": tps,
        "tps_measured": tps_measured,
        "tps_kind": tps_kind,
        "ctx_pct": used_pct,
        "ctx_window": ctx_window,
        "cache_pct": cache_pct,
        "credits": credits,
        "model": model,
    }


def build_line(st, payload=None, compact=False):
    """拼一行自然语言状态文本，用于 statusLine 和只有一行时的窄条。

    compact=True 省掉模型用时与工具用时（多行堆叠时这两项会把宽度撑满）。
    """
    m = metrics(st, payload)
    segs = []
    if st is not None:
        if not compact:
            segs.append(f"模型 {fmt_dur(m['model_s'])}")
            segs.append(f"工具 {fmt_dur(m['tool_s'])}")
        segs.append(f"{m['turns']}轮 {m['steps']}步")
    if m["tps"] is not None:
        segs.append(f"{m['tps']:.0f} tok/s{m['tps_kind']}")
    else:
        segs.append("tok/s —")
    if m["total_tokens"] is not None:
        segs.append(f"{fmt_tokens(m['total_tokens'])} tok")
    if m["ctx_pct"] is not None:
        # 刻意不用 ▓░ 这类块字符：本机控制台代码页是 GBK，它们不在 GBK 字符集内，
        # 一旦按本地编码解码就会变乱码。
        win = m["ctx_window"]
        if compact or not win:
            tail = ""
        elif win >= 1_000_000:
            tail = f" ({win/1e6:.1f}M)"
        else:
            tail = f" ({win/1000:.0f}k)"
        segs.append(f"上下文 {m['ctx_pct']:.1f}%{tail}")
    if m["cache_pct"] is not None:
        segs.append(f"缓存命中 {m['cache_pct']:.0f}%")
    if m["credits"] is not None:
        # 不用 ¢ 这种 GBK 编不出来的符号（本机实测 '¢'.encode('gbk') 直接抛
        # UnicodeEncodeError），窄条走 Tk 渲染虽然无碍，但同一份文本也会从
        # statusline 的 stdout 出去，留着就是编码隐患。
        segs.append(f"credits {m['credits']:.1f}" if compact
                    else f"credits {m['credits']:.2f}")
    if not compact:
        segs.append(m["model"])
    return " · ".join(segs)


# ---------------------------------------------------------------- 状态栏模式

def run_statusline():
    """作为 Qoder CN 的 statusLine 命令被调用。

    Qoder CN 会把一份 JSON 从 stdin 喂进来（实测取自 CLI 的 buildStatusLineInput）：
        session_id / transcript_path / cwd / model{id,display_name}
        cost{total_lines_added,total_lines_removed,total_credits}
        credits
        context_window{total_input_tokens,total_output_tokens,
                       context_window_size,used_percentage,remaining_percentage}
        workspace{current_dir,project_dir} / version / codebase / worktree
    """
    raw = ""
    try:
        raw = sys.stdin.read()
    except Exception:
        raw = ""
    payload = {}
    if raw.strip():
        try:
            payload = json.loads(raw)
        except Exception:
            payload = {}
    if not isinstance(payload, dict):
        payload = {}

    path = payload.get("transcript_path")
    if not path or not os.path.exists(path):
        path = find_active_transcript()[0]

    st = None
    if path and os.path.exists(path):
        st = SessionStats(path)
        st.read_increment()

    print(build_line(st, payload))
    return 0


def run_json():
    """把当前若干活跃会话的统计以 JSON 打到 stdout，供外部渲染器（WPF 透明条）轮询。

    单独开一个出口是为了让渲染端完全不碰解析与回归逻辑，口径只有一份。
    """
    top, total = find_active_transcripts(MAX_ROWS)
    out = []
    now = time.time()
    for mt, path in top:
        st = SessionStats(path)
        st.read_increment()
        m = metrics(st)
        sid = path.rsplit(os.sep, 1)[-1]
        if sid.endswith(".jsonl"):
            sid = sid[:-6]
        out.append({
            "session_id": sid,
            "title": session_title(sid, fallback=sid[:4]),
            "busy": bool(st.inflight()) or (now - mt) < ACTIVE_WINDOW_S,
            "turns": m["turns"],
            "steps": m["steps"],
            "tps": None if m["tps"] is None else round(m["tps"], 1),
            "tps_kind": m["tps_kind"],
            "total_tokens": None if m["total_tokens"] is None else round(m["total_tokens"]),
            "ctx_pct": None if m["ctx_pct"] is None else round(m["ctx_pct"], 1),
            "cache_pct": None if m["cache_pct"] is None else round(m["cache_pct"], 1),
            "credits": None if m["credits"] is None else round(m["credits"], 1),
        })
    print(json.dumps({"sessions": out, "extra": max(0, total - len(top)),
                      "ts": now}, ensure_ascii=False))
    return 0


def _session_dicts(top, cache, mt_map):
    """对一批 transcript 做增量解析，返回渲染用的字典列表。"""
    now = time.time()
    out = []
    for mt, path in top:
        st = cache.get(path)
        if st is None:
            st = SessionStats(path)
            cache[path] = st
            mt_map[path] = mt
            st.read_increment()
        elif mt != mt_map.get(path):
            mt_map[path] = mt
            st.read_increment()
        m = metrics(st)
        sid = path.rsplit(os.sep, 1)[-1]
        if sid.endswith(".jsonl"):
            sid = sid[:-6]
        out.append({
            "session_id": sid,
            "title": session_title(sid, fallback=sid[:4]),
            "busy": bool(st.inflight()) or (now - mt) < ACTIVE_WINDOW_S,
            "turns": m["turns"],
            "steps": m["steps"],
            "tps": None if m["tps"] is None else round(m["tps"], 1),
            "tps_kind": m["tps_kind"],
            "total_tokens": None if m["total_tokens"] is None else round(m["total_tokens"]),
            "ctx_pct": None if m["ctx_pct"] is None else round(m["ctx_pct"], 1),
            "cache_pct": None if m["cache_pct"] is None else round(m["cache_pct"], 1),
            "credits": None if m["credits"] is None else round(m["credits"], 1),
        })
    return out


def run_emit(path_out):
    """常驻采集：每秒把最新统计原子写入一个 JSON 文件，供外部渲染器读取。

    为什么不是让渲染端每秒起一个 python --json 子进程：那是冷启动，解释器要重来、
    transcript 要全量重读。本机实测 2026-10-15 一次 199ms（当时会话还少），
    2026-10-17 复测 393ms（5 次 390~397ms 的中位数，会话变长后成本跟着涨），
    每秒一次就是常年 39% 单核，而且解析缓存每次随进程丢掉。改成这里一次采集、
    增量解析、原子落盘，渲染端只做读文件：实测采集端占单核 0.39%。
    """
    cache, mt_map = {}, {}
    while True:
        try:
            top, total = find_active_transcripts(MAX_ROWS)
            payload = {"sessions": _session_dicts(top, cache, mt_map),
                       "extra": max(0, total - len(top)),
                       "ts": time.time()}
            tmp = path_out + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(payload, f, ensure_ascii=False)
            os.replace(tmp, path_out)          # 原子替换，读端不会拿到半截文件
        except Exception:
            pass
        time.sleep(1.0)


# ---------------------------------------------------------------- 命令行

def run_check():
    """无界面打印一次统计，用于验证解析结果。"""
    path, mt = find_active_transcript()
    if path is None:
        print("未找到会话记录")
        return 1
    st = SessionStats(path)
    st.read_increment()
    s = st.snapshot()
    print("transcript:", path)
    print("mtime     :", datetime.fromtimestamp(mt).isoformat())
    print("-" * 56)
    for k in (
        "model_s", "tool_s", "turns", "steps", "credits", "tps", "tps_kind",
        "tps_measured", "samples", "meas_dur", "est_dur",
        "last_tps", "ctx_ratio", "ctx_window", "total_tokens",
        "out_tokens", "in_tokens", "cache_read", "model_key", "usage_mode",
    ):
        v = s[k]
        if isinstance(v, float):
            v = round(v, 4)
        print(f"  {k:12s} = {v}")
    print(f"  坏行        = {st.lines_bad}")
    print("-" * 56)
    print(f"模型用时 {fmt_dur(s['model_s'])} | 工具用时 {fmt_dur(s['tool_s'])} | "
          f"{s['turns']}轮 {s['steps']}步 | credits {s['credits']:.3f}")
    return 0


def main():
    if "--statusline" in sys.argv:
        # Qoder CN 的 statusLine 走 shell 管道，Windows 下 python 默认按 GBK 编码
        # stdout，中文会变乱码，必须先切到 UTF-8。
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
        return run_statusline()
    if "--check" in sys.argv or "--once" in sys.argv:
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
        return run_check()
    if "--emit" in sys.argv:
        i = sys.argv.index("--emit")
        out = (sys.argv[i + 1] if i + 1 < len(sys.argv)
               else os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                 "bar_state.json"))
        return run_emit(out)
    if "--json" in sys.argv:
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
        return run_json()
    if "--fg" in sys.argv:
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass
        print("前台判定 (是否Qoder, 进程, 标题) =", foreground_is_qoder(os.getpid()))
        return 0
    # 这个文件现在只负责"读日志 + 算指标 + 写 JSON"，不再自带界面。
    # 界面是 bar_dcomp.exe（DirectComposition 渲染端），它读 bar_state.json。
    print("QoderMonitor 采集端（无界面）。用法：")
    print("  qoder_monitor.py --emit bar_state.json   常驻采集，供渲染端读取")
    print("  qoder_monitor.py --check                 打印一次统计")
    print("  qoder_monitor.py --json                  输出 JSON")
    print("  qoder_monitor.py --statusline            作为 Qoder 状态栏命令")
    return 2


if __name__ == "__main__":
    sys.exit(main())
