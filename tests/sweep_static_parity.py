# -*- coding: utf-8 -*-
"""一次性：把 projects 下全部静态 transcript 做 Python/C# 快照比对（parity 的扩大版）。

跳过最近 3 分钟内仍在写入的会话，避免边写边比造成的假失败。
"""
import glob
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, "single"))
import parity_test as pt  # noqa: E402

PROJECTS = os.path.join(os.path.expanduser("~"), ".qoder-cn", "projects")
files = []
for p in glob.glob(os.path.join(PROJECTS, "*", "*.jsonl")):
    try:
        if time.time() - os.path.getmtime(p) < 180:
            continue
    except OSError:
        continue
    files.append(p)
files.sort(key=lambda x: -os.path.getsize(x))
print("静态 transcript %d 个（跳过正在写入的）" % len(files))

ok = bad = 0
tps_rows = []
for p in files:
    py = pt.py_snapshot(p)
    rc, out, err = pt.run_cs("--snapshot", p)
    if rc != 0:
        print("FAIL(cs rc=%d) %s %s" % (rc, os.path.basename(p), err[:150]))
        bad += 1
        continue
    try:
        cs = json.loads(out)
    except Exception as e:
        print("FAIL(json) %s %s" % (os.path.basename(p), e))
        bad += 1
        continue
    if pt.compare_snapshot(os.path.basename(p)[:12], py, cs):
        ok += 1
    else:
        bad += 1
    tps_rows.append((os.path.basename(p)[:8], py.get("tps"), py.get("tps_kind"),
                     py.get("samples"), cs.get("samples")))

print("\nRESULT ok=%d bad=%d" % (ok, bad))
print("\n%-10s %-10s %-7s %s" % ("session", "tps", "kind", "总样本"))
for r in tps_rows[:40]:
    t = "%.1f" % r[1] if isinstance(r[1], (int, float)) else str(r[1])
    print("%-10s %-10s %-7s %s" % (r[0], t, r[2] or "-", r[3]))
