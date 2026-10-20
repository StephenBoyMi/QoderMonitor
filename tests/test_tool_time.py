# -*- coding: utf-8 -*-
"""工具用时只算"响应结束到最后一个 tool_result"这段，人离开的时间不能计进去。

场景（一份合成 transcript）：
  1. 人提问
  2. 模型 5 秒后回复，并调用一次工具
  3. 工具 7 秒后返回（tool_result）
  4. 模型 8 秒后给出结论
  5. 人离开 3 小时才接着问
  6. 模型 5 秒后回复

期望：工具用时 = 7 秒；模型用时 = 5+8+5 = 18 秒；那 3 小时两边都不算。
"""
import json
import os
import subprocess
import sys
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(HERE, "tool_time_idle.jsonl")
CS_EXE = os.path.join(ROOT, "single", "qoder_data.exe")
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "single"))


def ts(t):
    return t.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + \
        "%03dZ" % (t.microsecond // 1000)


def rows():
    t0 = datetime(2026, 10, 20, 0, 0, 0, tzinfo=timezone.utc)
    t = t0
    out = []
    out.append({"type": "user", "timestamp": ts(t), "promptId": "p1",
                "message": {"content": [{"type": "text", "text": "跑一下"}]}})
    t += timedelta(seconds=5)
    out.append({"type": "assistant", "timestamp": ts(t),
                "message": {"id": "r1", "model": "m",
                            "content": [{"type": "tool_use", "id": "tu1",
                                         "name": "run", "input": {"cmd": "ls"}}]}})
    t += timedelta(seconds=7)
    out.append({"type": "user", "timestamp": ts(t),
                "message": {"content": [{"type": "tool_result", "tool_use_id": "tu1",
                                         "content": "ok"}]}})
    t += timedelta(seconds=8)
    out.append({"type": "assistant", "timestamp": ts(t),
                "message": {"id": "r2", "model": "m",
                            "content": [{"type": "text", "text": "好了"}],
                            "usage": {"output_tokens": 100}}})
    t += timedelta(hours=3)
    out.append({"type": "user", "timestamp": ts(t), "promptId": "p2",
                "message": {"content": [{"type": "text", "text": "继续"}]}})
    t += timedelta(seconds=5)
    out.append({"type": "assistant", "timestamp": ts(t),
                "message": {"id": "r3", "model": "m",
                            "content": [{"type": "text", "text": "好"}],
                            "usage": {"output_tokens": 80}}})
    t += timedelta(minutes=1)
    out.append({"type": "user", "timestamp": ts(t), "promptId": "p3",
                "message": {"content": [{"type": "text", "text": "结束"}]}})
    return out


def main():
    with open(OUT, "w", encoding="utf-8") as f:
        for o in rows():
            f.write(json.dumps(o, ensure_ascii=False) + "\n")

    import qoder_monitor as qm
    st = qm.SessionStats(OUT)
    st.read_increment()
    py_tool, py_model = st.tool_s, st.model_s

    p = subprocess.run([CS_EXE, "--snapshot", OUT], capture_output=True,
                       encoding="utf-8", errors="replace", timeout=120)
    cs = json.loads(p.stdout)
    cs_tool, cs_model = cs["tool_s"], cs["model_s"]

    print("总跨度      : 3 小时 1 分（其中 3 小时在等人）")
    print("Python      : 工具 %.2fs  模型 %.2fs" % (py_tool, py_model))
    print("C#          : 工具 %.2fs  模型 %.2fs" % (cs_tool, cs_model))
    ok = (abs(py_tool - 7.0) < 1e-6 and abs(py_model - 18.0) < 1e-6
          and abs(cs_tool - py_tool) < 2e-3 and abs(cs_model - py_model) < 2e-3)
    print("\n期望        : 工具 7.00s  模型 18.00s（3 小时不计）")
    print("RESULT", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
