# -*- coding: utf-8 -*-
"""稳健斜率（Theil–Sen）：单个离群请求不能把显示的 tok/s 拽走。

场景：12 条正常请求（400..620 token / 3.0..4.65 秒，恒为 133.3 tok/s），
外加 1 条又长又慢的请求（3000 token / 200 秒）。最小二乘会被这 1 条拽到 ~50，
Theil–Sen 的两两斜率中位数仍是 133.3。

跑法：python test_robust_slope.py
"""
import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(HERE, "robust_slope.jsonl")
CS_EXE = os.path.join(ROOT, "single", "qoder_data.exe")
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "single"))

FAST = [(400 + 20 * i, 3.0 + 0.15 * i) for i in range(12)]
SLOW = (3000, 200.0)


def ts(t):
    return t.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + \
        "%03dZ" % (t.microsecond // 1000)


def rows():
    t = datetime(2026, 10, 20, 0, 0, 0, tzinfo=timezone.utc)
    out = []
    for i, (tok, dur) in enumerate(FAST + [SLOW]):
        out.append({"type": "user", "timestamp": ts(t), "promptId": "p%d" % i,
                    "message": {"content": [{"type": "text", "text": "go"}]}})
        t += timedelta(seconds=dur)
        out.append({"type": "assistant", "timestamp": ts(t),
                    "message": {"id": "r%d" % i, "model": "m",
                                "content": [{"type": "text", "text": "x"}],
                                "usage": {"output_tokens": tok, "input_tokens": 1000}}})
        t += timedelta(seconds=0.5)
    # 末尾补一条记录，让最后一条响应结算成样本（不闭合的组不计）
    out.append({"type": "user", "timestamp": ts(t), "promptId": "pend",
                "message": {"content": [{"type": "text", "text": "done"}]}})
    return out


def ols(pts):
    n = len(pts)
    mx = sum(x for x, _y in pts) / n
    my = sum(y for _x, y in pts) / n
    sxx = sum((x - mx) ** 2 for x, _y in pts)
    sxy = sum((x - mx) * (y - my) for x, y in pts)
    return 1.0 / (sxy / sxx)


def main():
    with open(OUT, "w", encoding="utf-8") as f:
        for o in rows():
            f.write(json.dumps(o, ensure_ascii=False) + "\n")

    import qoder_monitor as qm
    st = qm.SessionStats(OUT)
    st.read_increment()
    py_tps, py_n, py_all = st.decode_tps()

    p = subprocess.run([CS_EXE, "--snapshot", OUT], capture_output=True,
                       encoding="utf-8", errors="replace", timeout=120)
    cs = json.loads(p.stdout)

    print("样本: 12 条 133.3 tok/s + 1 条 3000tok/200s")
    print("最小二乘会报     : %.1f tok/s（被那一条拽走）" % ols(FAST + [SLOW]))
    print("Python Theil-Sen : %.1f tok/s (n=%d, 真值=%s)" % (py_tps, py_n, py_all))
    print("C#     Theil-Sen : %.1f tok/s" % cs["tps"])

    ok = (abs(py_tps - 133.33) < 1.5
          and abs(cs["tps"] - py_tps) < 2e-3
          and cs["tps_kind"] == "")
    print("\n期望: 133.3 tok/s（离群点不该影响）")
    print("RESULT", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())