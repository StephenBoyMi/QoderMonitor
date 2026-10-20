# -*- coding: utf-8 -*-
"""生成/校验一个"刚换模型"的最小场景：切换前 10 条快样本、切换后 3 条慢样本，
全部落在同一个 10 分钟活跃窗内，用来验证显示值不会被切换前的快样本顶高。

跑法：python test_switch_window.py
它写出 tests/switch_dilution.jsonl，再分别用 Python 版和 single/qoder_data.exe 读一遍。
"""
import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(HERE, "switch_dilution.jsonl")
CS_EXE = os.path.join(ROOT, "single", "qoder_data.exe")
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "single"))


def ts(t):
    return t.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + \
        "%03dZ" % (t.microsecond // 1000)


def rows():
    t = datetime(2026, 10, 20, 0, 0, 0, tzinfo=timezone.utc)
    out = []
    seq = ([("fast-model", 500, 2.0)] * 10) + ([("slow-model", 100, 8.0)] * 3)
    for i, (model, tok, dur) in enumerate(seq):
        out.append({"type": "user", "timestamp": ts(t), "promptId": "p%d" % i,
                    "message": {"content": [{"type": "text", "text": "go"}]}})
        t += timedelta(seconds=dur)
        out.append({"type": "assistant", "timestamp": ts(t),
                    "message": {"id": "r%d" % i, "model": model,
                                "content": [{"type": "text", "text": "x" * tok}],
                                "usage": {"output_tokens": tok, "input_tokens": 1000}}})
        t += timedelta(seconds=1)
    return out


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        for o in rows():
            f.write(json.dumps(o, ensure_ascii=False) + "\n")

    import qoder_monitor as qm
    st = qm.SessionStats(OUT)
    st.read_increment()
    py_tps, py_meas, py_kind = (lambda m: (m["tps"], m["tps_measured"], m["tps_kind"]))(
        qm.metrics(st))
    win = st.current_window()
    models = sorted({r[4] for r in win})

    p = subprocess.run([CS_EXE, "--snapshot", OUT], capture_output=True,
                       encoding="utf-8", errors="replace", timeout=120)
    cs = json.loads(p.stdout)

    print("样本总数        :", len(st.samples), "（10 条 fast-model + 3 条 slow-model）")
    print("活跃窗内模型      :", models)
    print("Python  tps=%s kind=%r measured=%s" % (
        None if py_tps is None else round(py_tps, 1), py_kind, py_meas))
    print("C#      tps=%s kind=%r measured=%s" % (
        None if cs.get("tps") is None else round(cs["tps"], 1),
        cs.get("tps_kind"), cs.get("tps_measured")))

    # 期望：只用 slow-model 的 3 条样本；3 条不足回归门槛，退回端到端 100/8=12.5，
    # 且绝不出现 fast-model 的 250 tok/s。
    ok = (models == ["slow-model"]
          and abs(py_tps - 12.5) < 0.01 and py_kind == "(e2e)"
          and abs(cs["tps"] - py_tps) < 2e-3 and cs["tps_kind"] == py_kind)
    print("\nRESULT", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
