using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace QoderMonitor
{
    static class Program
    {
        static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            if (Has(args, "--check") || Has(args, "--once"))
                return RunCheck();
            if (Has(args, "--json"))
                return RunJson();
            if (Has(args, "--statusline"))
                return RunStatusline();
            if (Has(args, "--snapshot"))
                return RunSnapshot(ArgAfter(args, "--snapshot"));
            if (Has(args, "--title"))
                return RunTitle(ArgAfter(args, "--title"));
            if (Has(args, "--emit"))
                return RunEmit(ArgAfter(args, "--emit"));

            Console.WriteLine("QoderMonitor C# 数据层（开发验证版）");
            Console.WriteLine("  qoder_data.exe --check                 打印当前最活跃会话");
            Console.WriteLine("  qoder_data.exe --json                  输出前两个活跃会话");
            Console.WriteLine("  qoder_data.exe --snapshot <file.jsonl> 打印单个文件快照（parity 用）");
            Console.WriteLine("  qoder_data.exe --statusline            stdin 读 Qoder payload，输出一行");
            Console.WriteLine("  qoder_data.exe --title <session_id>    查询标题");
            Console.WriteLine("  qoder_data.exe --emit <file.json>      常驻采集");
            return 2;
        }

        static bool Has(string[] args, string key)
        {
            for (int i = 0; i < args.Length; i++) if (args[i] == key) return true;
            return false;
        }

        static string ArgAfter(string[] args, string key)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }

        static int RunCheck()
        {
            Finding f = SessionFinder.FindActiveTranscript();
            if (f == null)
            {
                Console.WriteLine("未找到会话记录");
                return 1;
            }
            SessionStats st = new SessionStats(f.Path);
            st.ReadIncrement();
            Metrics m = MetricCalculator.Compute(st, null);

            Console.WriteLine("transcript: " + f.Path);
            Console.WriteLine("mtime     : " + DateTimeOffset.FromUnixTimeMilliseconds(
                (long)(f.Mtime * 1000)).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff"));
            Console.WriteLine(new string('-', 56));
            Print("model_s", Round4(st.ModelS));
            Print("tool_s", Round4(st.ToolS));
            Print("turns", m.Turns);
            Print("steps", m.Steps);
            Print("credits", Round4(st.Credits));
            Print("tps", m.Tps.HasValue ? Round4(m.Tps.Value) : (object)null);
            Print("tps_kind", m.TpsKind);
            Print("tps_measured", m.TpsMeasured);
            Print("samples", st.Samples.Count);
            Print("meas_dur", Round4(st.MeasDur));
            Print("est_dur", Round4(st.EstDur));
            Print("last_tps", st.LastReqTps.HasValue ? Round4(st.LastReqTps.Value) : (object)null);
            Print("ctx_ratio", st.CtxRatio.HasValue ? Round4(st.CtxRatio.Value) : (object)null);
            Print("ctx_window", st.CtxWindow.HasValue ? (object)st.CtxWindow.Value : null);
            Print("total_tokens", m.TotalTokens.HasValue ? Round4(m.TotalTokens.Value) : (object)null);
            Print("out_tokens", Round4(st.MeasTok + st.EstTok));
            Print("in_tokens", Round4(st.InTokensTrue));
            Print("cache_read", Round4(st.CacheReadTrue));
            Print("model_key", st.ModelKey);
            Print("usage_mode", UsageMode(m));
            Console.WriteLine("  坏行        = " + st.LinesBad.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine(new string('-', 56));
            Console.WriteLine("模型用时 " + QmUtil.FmtDur(st.ModelS)
                + " | 工具用时 " + QmUtil.FmtDur(st.ToolS)
                + " | " + m.Turns + "轮 " + m.Steps + "步"
                + " | credits " + st.Credits.ToString("0.000", CultureInfo.InvariantCulture));
            return 0;
        }

        static int RunJson()
        {
            List<Finding> top = SessionFinder.FindActiveTranscripts(2, 30.0);
            List<Finding> all = SessionFinder.FindActiveTranscripts(1000, 30.0);
            double now = QmUtil.UnixNow();
            List<Dictionary<string, object>> rows = RowFactory.Fresh(top, now);
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["sessions"] = rows;
            root["extra"] = Math.Max(0, all.Count - top.Count);
            root["ts"] = now;
            Console.WriteLine(QmJson.Serialize(root));
            return 0;
        }

        static int RunSnapshot(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Console.WriteLine("missing file: " + (path ?? ""));
                return 1;
            }
            SessionStats st = new SessionStats(path);
            st.ReadIncrement();
            Metrics m = MetricCalculator.Compute(st, null);
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["model_s"] = Round4(st.ModelS);
            d["tool_s"] = Round4(st.ToolS);
            d["turns"] = m.Turns;
            d["steps"] = m.Steps;
            d["credits"] = Round4(st.Credits);
            d["tps"] = m.Tps.HasValue ? (object)Round4(m.Tps.Value) : null;
            d["tps_kind"] = m.TpsKind;
            d["tps_measured"] = m.TpsMeasured;
            d["samples"] = st.Samples.Count;
            d["meas_dur"] = Round4(st.MeasDur);
            d["est_dur"] = Round4(st.EstDur);
            d["last_tps"] = st.LastReqTps.HasValue ? (object)Round4(st.LastReqTps.Value) : null;
            d["ctx_ratio"] = st.CtxRatio.HasValue ? (object)Round4(st.CtxRatio.Value) : null;
            d["ctx_window"] = st.CtxWindow.HasValue ? (object)st.CtxWindow.Value : null;
            d["total_tokens"] = m.TotalTokens.HasValue ? (object)Round4(m.TotalTokens.Value) : null;
            d["out_tokens"] = Round4(st.MeasTok + st.EstTok);
            d["in_tokens"] = Round4(st.InTokensTrue);
            d["cache_read"] = Round4(st.CacheReadTrue);
            d["model_key"] = st.ModelKey;
            d["usage_mode"] = UsageMode(m);
            d["bad_lines"] = st.LinesBad;
            Console.WriteLine(QmJson.Serialize(d));
            return 0;
        }

        static int RunTitle(string sid)
        {
            if (string.IsNullOrEmpty(sid)) return 1;
            string fallback = sid.Length > 4 ? sid.Substring(0, 4) : sid;
            Console.WriteLine(TitleStore.Get(sid, fallback));
            return 0;
        }

        static int RunStatusline()
        {
            string raw = "";
            try { raw = Console.In.ReadToEnd(); } catch { }
            Dictionary<string, object> payload = QmJson.ParseObject(raw);
            if (payload == null) payload = new Dictionary<string, object>();
            string path = null;
            object p;
            if (payload.TryGetValue("transcript_path", out p)) path = QmUtil.Str(p);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Finding f = SessionFinder.FindActiveTranscript();
                path = f != null ? f.Path : null;
            }
            SessionStats st = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                st = new SessionStats(path);
                st.ReadIncrement();
            }
            Console.WriteLine(MetricCalculator.BuildLine(st, payload, false));
            return 0;
        }

        static int RunEmit(string path)
        {
            if (string.IsNullOrEmpty(path)) path = "bar_state.json";
            SessionCache cache = new SessionCache();
            while (true)
            {
                try
                {
                    List<Finding> top = SessionFinder.FindActiveTranscripts(2, 30.0);
                    List<Finding> all = SessionFinder.FindActiveTranscripts(1000, 30.0);
                    double now = QmUtil.UnixNow();
                    Dictionary<string, object> root = new Dictionary<string, object>();
                    root["sessions"] = cache.Build(top, now);
                    root["extra"] = Math.Max(0, all.Count - top.Count);
                    root["ts"] = now;
                    string tmp = path + ".tmp";
                    File.WriteAllText(tmp, QmJson.Serialize(root), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch { }
                Thread.Sleep(1000);
            }
        }

        static string UsageMode(Metrics m)
        {
            if (!string.IsNullOrEmpty(m.TpsKind)) return m.TpsKind;
            return m.TpsMeasured ? "实测" : "估算";
        }

        static double Round4(double v) { return Math.Round(v, 4, MidpointRounding.ToEven); }

        static void Print(string key, object v)
        {
            string text;
            if (v == null) text = "None";
            else if (v is double) text = ((double)v).ToString("0.####", CultureInfo.InvariantCulture);
            else if (v is bool) text = ((bool)v) ? "True" : "False";
            else text = Convert.ToString(v, CultureInfo.InvariantCulture);
            Console.WriteLine("  " + key.PadRight(12) + " = " + text);
        }
    }
}
