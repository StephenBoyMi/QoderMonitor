"""C# 数据层与 Python 版的 parity 测试（开发用，不参与发布）。

用法：python parity_test.py
它会冻结若干 transcript 的副本，对同一份数据分别跑 Python 的
SessionStats/--statusline 和 C# 的 qoder_data.exe，再逐项比较。
"""

from __future__ import annotations

import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

import qoder_monitor as qm  # noqa: E402

CS_EXE = os.path.join(HERE, "qoder_data.exe")
PROJECTS = os.path.join(os.path.expanduser("~"), ".qoder-cn", "projects")
FLOAT_TOL = 2e-3


def run_cs(*args, stdin=None):
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    p = subprocess.run(
        [CS_EXE] + list(args), input=stdin, capture_output=True,
        encoding="utf-8", errors="replace", timeout=120, env=env,
    )
    return p.returncode, p.stdout, p.stderr


def py_snapshot(path):
    st = qm.SessionStats(path)
    st.read_increment()
    s = st.snapshot()
    s["bad_lines"] = st.lines_bad
    return s


def frozen_files(tmp):
    rows = []
    for p in glob.glob(os.path.join(PROJECTS, "*", "*.jsonl")):
        try:
            rows.append((os.path.getmtime(p), os.path.getsize(p), p))
        except OSError:
            continue
    rows.sort(reverse=True)
    chosen = []
    total = 0
    for mt, sz, p in rows:
        if len(chosen) >= 8:
            break
        if total + sz > 55 * 1024 * 1024 and chosen:
            continue
        dst = os.path.join(tmp, os.path.basename(p))
        shutil.copy2(p, dst)
        chosen.append((p, dst))
        total += sz
    return chosen


def approx(a, b):
    if a is None or b is None:
        return a is None and b is None
    try:
        return abs(float(a) - float(b)) <= FLOAT_TOL
    except (TypeError, ValueError):
        return a == b


def compare_snapshot(label, py, cs):
    problems = []
    fields = [
        "model_s", "tool_s", "turns", "steps", "credits", "tps",
        "tps_kind", "tps_measured", "samples", "meas_dur", "est_dur",
        "last_tps", "ctx_ratio", "ctx_window", "total_tokens",
        "out_tokens", "in_tokens", "cache_read", "model_key",
        "usage_mode", "bad_lines",
    ]
    for k in fields:
        if k not in cs:
            problems.append(f"{k}: C# 缺字段")
            continue
        if isinstance(py.get(k), (int, float)) and not isinstance(py.get(k), bool):
            if not approx(py.get(k), cs.get(k)):
                problems.append(f"{k}: py={py.get(k)!r} cs={cs.get(k)!r}")
        elif py.get(k) != cs.get(k):
            problems.append(f"{k}: py={py.get(k)!r} cs={cs.get(k)!r}")
    if problems:
        print(f"FAIL {label}")
        for p in problems:
            print("  ", p)
        return False
    print(f"PASS {label}")
    return True


def main():
    if not os.path.exists(CS_EXE):
        print("missing", CS_EXE)
        return 1
    tmp = tempfile.mkdtemp(prefix="qodermon_parity_")
    ok = True
    try:
        files = frozen_files(tmp)
        print(f"frozen {len(files)} transcripts in {tmp}")
        for src, path in files:
            label = os.path.basename(path)
            py = py_snapshot(path)
            rc, out, err = run_cs("--snapshot", path)
            if rc != 0:
                print(f"FAIL {label}: C# rc={rc} {err[:200]}")
                ok = False
                continue
            try:
                cs = json.loads(out)
            except Exception as e:
                print(f"FAIL {label}: C# JSON {e}: {out[:200]}")
                ok = False
                continue
            ok = compare_snapshot(label, py, cs) and ok

            sid = os.path.splitext(os.path.basename(src))[0]
            py_title = qm.session_title(sid, sid[:4])
            rc, out, err = run_cs("--title", sid)
            cs_title = out.strip()
            if rc != 0 or py_title != cs_title:
                print(f"FAIL title {sid}: py={py_title!r} cs={cs_title!r}")
                ok = False
            else:
                print(f"PASS title {sid}: {py_title!r}")

        # statusline 同一份 payload 逐字比较
        if files:
            payload = {
                "transcript_path": files[0][1],
                "model": {"display_name": "parity-model"},
                "cost": {"total_credits": 12.34},
                "context_window": {"context_window_size": 1000000,
                                   "used_percentage": 54.5},
            }
            raw = json.dumps(payload)
            env = dict(os.environ, PYTHONIOENCODING="utf-8")
            p = subprocess.run([sys.executable, os.path.join(ROOT, "qoder_monitor.py"),
                                "--statusline"], input=raw, capture_output=True,
                               encoding="utf-8", errors="replace", timeout=120, env=env)
            rc, out, err = run_cs("--statusline", stdin=raw)
            py_line = p.stdout.strip()
            cs_line = out.strip()
            if py_line != cs_line:
                print("FAIL statusline")
                print("  py:", repr(py_line))
                print("  cs:", repr(cs_line))
                ok = False
            else:
                print("PASS statusline")

        # JSON 行（允许 ts 不同，比较同 session 的字段）
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        p = subprocess.run([sys.executable, os.path.join(ROOT, "qoder_monitor.py"), "--json"],
                           capture_output=True, encoding="utf-8", errors="replace",
                           timeout=120, env=env)
        rc, out, err = run_cs("--json")
        try:
            pyj = json.loads(p.stdout)
            csj = json.loads(out)
            pys = {s["session_id"]: s for s in pyj.get("sessions", [])}
            css = {s["session_id"]: s for s in csj.get("sessions", [])}
            if set(pys) != set(css):
                print("FAIL json sessions:", set(pys), set(css))
                ok = False
            else:
                bad = False
                for sid in pys:
                    for k, v in pys[sid].items():
                        if k == "ts":
                            continue
                        if isinstance(v, (int, float)) and not isinstance(v, bool):
                            if not approx(v, css[sid].get(k)):
                                print(f"FAIL json {sid} {k}: {v!r} vs {css[sid].get(k)!r}")
                                bad = True
                        elif v != css[sid].get(k):
                            print(f"FAIL json {sid} {k}: {v!r} vs {css[sid].get(k)!r}")
                            bad = True
                if bad:
                    ok = False
                else:
                    print("PASS json")
        except Exception as e:
            print("FAIL json:", e, out[:200])
            ok = False

        print("RESULT", "PASS" if ok else "FAIL")
        return 0 if ok else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
