using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace QoderMonitor
{
    // ---------------------------------------------------------------- 路径

    public static class QmPaths
    {
        public static readonly string Home =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string QoderHome = FindQoderHome();
        public static readonly string ProjectsDir = Path.Combine(QoderHome, "projects");
        public static readonly string TitleDb = FindTitleDb();

        static string FindQoderHome()
        {
            string env = Environment.GetEnvironmentVariable("QODER_MONITOR_HOME");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            string cn = Path.Combine(Home, ".qoder-cn");
            if (Directory.Exists(Path.Combine(cn, "projects"))) return cn;
            string intl = Path.Combine(Home, ".qoder");
            if (Directory.Exists(Path.Combine(intl, "projects"))) return intl;
            return cn;
        }

        static string FindTitleDb()
        {
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData))
                appData = Path.Combine(Home, "AppData", "Roaming");
            return Path.Combine(appData, "com.qodercn.app.stable", "main.sqlite");
        }
    }

    // ---------------------------------------------------------------- JSON / 工具

    public static class QmJson
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer
        {
            MaxJsonLength = 1024 * 1024 * 64
        };

        public static Dictionary<string, object> ParseObject(string text)
        {
            try
            {
                object o = Ser.DeserializeObject(text);
                return o as Dictionary<string, object>;
            }
            catch { return null; }
        }

        public static string Serialize(object o)
        {
            return Ser.Serialize(o);
        }

        public static string DumpPython(object o)
        {
            StringBuilder sb = new StringBuilder();
            DumpPython(o, sb);
            return sb.ToString();
        }

        static void DumpPython(object o, StringBuilder sb)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string) { AppendQuoted((string)o, sb); return; }
            if (o is bool) { sb.Append(((bool)o) ? "true" : "false"); return; }
            if (QmUtil.IsNumber(o))
            {
                if (o is double || o is float || o is decimal)
                {
                    double d = QmUtil.ToDouble(o);
                    if (Math.Floor(d) == d && Math.Abs(d) < 1e15)
                        sb.Append(d.ToString("0.0", CultureInfo.InvariantCulture));
                    else
                        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(Convert.ToInt64(o, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture));
                }
                return;
            }
            IDictionary<string, object> dict = o as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    AppendQuoted(kv.Key, sb);
                    sb.Append(": ");
                    DumpPython(kv.Value, sb);
                }
                sb.Append('}');
                return;
            }
            IList list = o as IList;
            if (list != null)
            {
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    DumpPython(list[i], sb);
                }
                sb.Append(']');
                return;
            }
            AppendQuoted(Convert.ToString(o, CultureInfo.InvariantCulture), sb);
        }

        static void AppendQuoted(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    public static class QmUtil
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double? ParseTs(object v)
        {
            string s = v as string;
            if (s == null) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                return (dto.UtcDateTime - Epoch).TotalSeconds;
            return null;
        }

        public static bool IsCjk(char ch)
        {
            int o = ch;
            return (o >= 0x4E00 && o <= 0x9FFF)
                || (o >= 0x3400 && o <= 0x4DBF)
                || (o >= 0x3040 && o <= 0x30FF)
                || (o >= 0xAC00 && o <= 0xD7AF)
                || (o >= 0x3000 && o <= 0x303F)
                || (o >= 0xFF00 && o <= 0xFFEF);
        }

        public static double EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0.0;
            int cjk = 0, other = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                // Python 按 Unicode 码点计数；代理对在 Python 里算 1 个“其他”字符。
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    other++;
                    i++;
                }
                else if (IsCjk(ch)) cjk++;
                else other++;
            }
            return cjk / 1.4 + other / 4.0;
        }

        public static IList BlocksOf(Dictionary<string, object> message)
        {
            if (message == null) return new ArrayList();
            object content;
            if (!message.TryGetValue("content", out content)) return new ArrayList();
            IList list = content as IList;
            if (list != null) return list;
            string s = content as string;
            if (s != null)
            {
                ArrayList one = new ArrayList();
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["type"] = "text";
                b["text"] = s;
                one.Add(b);
                return one;
            }
            return new ArrayList();
        }

        public static bool IsNumber(object o)
        {
            return o is int || o is long || o is short || o is byte || o is sbyte
                || o is uint || o is ulong || o is ushort
                || o is float || o is double || o is decimal;
        }

        public static double ToDouble(object o)
        {
            return Convert.ToDouble(o, CultureInfo.InvariantCulture);
        }

        public static double? Num(object o)
        {
            if (!IsNumber(o)) return null;
            return ToDouble(o);
        }

        public static Dictionary<string, object> AsDict(object o)
        {
            IDictionary<string, object> d = o as IDictionary<string, object>;
            if (d == null) return null;
            Dictionary<string, object> result = d as Dictionary<string, object>;
            if (result != null) return result;
            return new Dictionary<string, object>(d);
        }

        public static string Str(object o)
        {
            return o as string;
        }

        public static double UnixNow()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }

        public static string FmtDur(double? sec)
        {
            double s = (sec.HasValue && sec.Value >= 0) ? sec.Value : 0.0;
            if (s < 60.0)
                return Math.Round(s, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture) + "秒";
            int total = (int)Math.Round(s, MidpointRounding.ToEven);
            int m = total / 60;
            int ss = total % 60;
            if (m < 60)
                return m.ToString(CultureInfo.InvariantCulture) + "分"
                    + ss.ToString("00", CultureInfo.InvariantCulture) + "秒";
            int h = m / 60;
            m = m % 60;
            return h.ToString(CultureInfo.InvariantCulture) + "时"
                + m.ToString("00", CultureInfo.InvariantCulture) + "分";
        }

        public static string FmtTokens(double? n)
        {
            if (!n.HasValue) return "—";
            double v = n.Value;
            if (v >= 1e6) return (v / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return Math.Round(v, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------- 标题库

    public static class TitleStore
    {
        const int SQLITE_OK = 0;
        const int SQLITE_ROW = 100;
        const int SQLITE_OPEN_READONLY = 1;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_bind_text(IntPtr stmt, int index, byte[] text, int n, IntPtr destructor);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int sqlite3_close_v2(IntPtr db);

        class Entry { public string Title; public double At; }
        static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();
        const double Ttl = 5.0;

        public static string Get(string sid, string fallback)
        {
            if (string.IsNullOrEmpty(sid)) return fallback;
            Entry hit;
            double now = QmUtil.UnixNow();
            if (Cache.TryGetValue(sid, out hit) && now - hit.At < Ttl) return hit.Title;

            string title = Query(sid);
            if (string.IsNullOrEmpty(title))
                return hit != null ? hit.Title : fallback;
            if (title.Length > 14) title = title.Substring(0, 13) + "…";
            Cache[sid] = new Entry { Title = title, At = now };
            return title;
        }

        static string Query(string sid)
        {
            if (!File.Exists(QmPaths.TitleDb)) return null;
            IntPtr db = IntPtr.Zero;
            IntPtr stmt = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(Encoding.UTF8.GetBytes(QmPaths.TitleDb + "\0"),
                        out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
                    return null;
                sqlite3_busy_timeout(db, 1000);
                byte[] sql = Encoding.UTF8.GetBytes(
                    "SELECT title, extra_json FROM chat_sessions WHERE session_id=?\0");
                if (sqlite3_prepare_v2(db, sql, sql.Length - 1, out stmt, IntPtr.Zero) != SQLITE_OK)
                    return null;
                byte[] key = Encoding.UTF8.GetBytes(sid);
                sqlite3_bind_text(stmt, 1, key, key.Length, new IntPtr(-1));
                if (sqlite3_step(stmt) != SQLITE_ROW) return null;
                string title = Column(stmt, 0);
                string extra = Column(stmt, 1);
                if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(extra))
                {
                    Dictionary<string, object> d = QmJson.ParseObject(extra);
                    if (d != null)
                    {
                        object pt;
                        if (d.TryGetValue("provisionalTitle", out pt))
                            title = QmUtil.Str(pt);
                    }
                }
                return title == null ? null : title.Trim();
            }
            catch { return null; }
            finally
            {
                if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
            }
        }

        static string Column(IntPtr stmt, int col)
        {
            IntPtr p = sqlite3_column_text(stmt, col);
            if (p == IntPtr.Zero) return null;
            int n = sqlite3_column_bytes(stmt, col);
            if (n <= 0) return "";
            byte[] b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }
    }

    // ---------------------------------------------------------------- 会话状态机

    public class Sample
    {
        public double Tok;
        public double Dur;
        public bool Real;
        // End/Model 是"活跃窗 + 同模型"两条过滤的依据：换模型后旧请求不该再进回归池。
        public double End;
        public string Model;
        public Sample(double tok, double dur, bool real, double end, string model)
        {
            Tok = tok; Dur = dur; Real = real; End = end; Model = model;
        }
    }

    public class DecodeResult
    {
        public bool HasValue;
        public double Tps;
        public int N;
        public bool AllMeasured;
        public DecodeResult(bool has, double tps, int n, bool all)
        {
            HasValue = has; Tps = tps; N = n; AllMeasured = all;
        }
    }

    public class InflightInfo
    {
        public double Dur;
        public double Tokens;
        public double? Tps;
        public bool Thinking;
    }

    public class SessionStats
    {
        class Group
        {
            public double? Start;
            public double? End;
            public double CjkEst;
            public Dictionary<string, object> Usage;
            public string ReqId;
            public string Model;
        }

        // 活跃窗长度与回归最少样本数：窗内只保留当前模型的样本。
        public const double ActiveWindowS = 600.0;
        public const int DecodeMinSamples = 8;

        public readonly string Path;
        public long Offset;
        public double ModelS, ToolS, Credits;
        public int Steps;
        public readonly HashSet<string> TurnIds = new HashSet<string>(StringComparer.Ordinal);
        public int ErrSteps;
        public double OutTokensTrue, OutTokensEst, InTokensTrue, CacheReadTrue;
        public bool HasTrueUsage;
        public double MeasTok, MeasDur, EstTok, EstDur;
        public readonly List<Sample> Samples = new List<Sample>();
        public double? CtxRatio;
        public long? CtxWindow;
        public string ModelKey;
        public double? LastReqS, LastReqTps;
        readonly HashSet<string> seenReq = new HashSet<string>(StringComparer.Ordinal);
        Group cur;
        double? lastTs, toolStart, toolEnd;
        public int LinesBad;

        public SessionStats(string path)
        {
            Path = path;
            ResetState();
        }

        void ResetState()
        {
            Offset = 0;
            ModelS = ToolS = Credits = 0;
            Steps = 0; ErrSteps = 0;
            TurnIds.Clear(); seenReq.Clear();
            OutTokensTrue = OutTokensEst = InTokensTrue = CacheReadTrue = 0;
            HasTrueUsage = false;
            MeasTok = MeasDur = EstTok = EstDur = 0;
            Samples.Clear();
            CtxRatio = null; CtxWindow = null; ModelKey = null;
            LastReqS = null; LastReqTps = null;
            cur = null; lastTs = null; toolStart = null; toolEnd = null;
            LinesBad = 0;
        }

        void OpenGroup(double? ts)
        {
            Group g = new Group();
            g.Start = lastTs.HasValue ? lastTs : ts;
            g.End = ts;
            g.CjkEst = 0;
            g.Usage = null;
            g.ReqId = null;
            cur = g;
        }

        void CloseGroup(double? nextTs)
        {
            Group g = cur;
            cur = null;
            if (g == null) return;
            double? dur = null;
            if (g.Start.HasValue && g.End.HasValue && g.End.Value >= g.Start.Value)
            {
                dur = g.End.Value - g.Start.Value;
                ModelS += dur.Value;
            }
            Steps++;

            Dictionary<string, object> u = g.Usage ?? new Dictionary<string, object>();
            object ridObj;
            string reqId = u.TryGetValue("request_id", out ridObj) ? QmUtil.Str(ridObj) : null;
            if (string.IsNullOrEmpty(reqId)) reqId = g.ReqId;
            if (!string.IsNullOrEmpty(reqId) && seenReq.Add(reqId))
            {
                object c;
                if (u.TryGetValue("credits", out c) && QmUtil.IsNumber(c))
                    Credits += QmUtil.ToDouble(c);
            }

            double dur0 = dur.HasValue ? dur.Value : 0.0;
            double? ot = GetNum(u, "output_tokens");
            bool real = ot.HasValue && ot.Value > 0;
            double tok;
            if (real)
            {
                HasTrueUsage = true;
                OutTokensTrue += ot.Value;
                MeasTok += ot.Value;
                MeasDur += dur0;
                double? it = GetNum(u, "input_tokens");
                if (it.HasValue) InTokensTrue += it.Value;
                double? cr = GetNum(u, "cache_read_input_tokens");
                if (cr.HasValue) CacheReadTrue += cr.Value;
                tok = ot.Value;
            }
            else
            {
                EstTok += g.CjkEst;
                EstDur += dur0;
                tok = g.CjkEst;
            }
            OutTokensEst += g.CjkEst;

            double? ratio = GetNum(u, "context_usage_ratio");
            if (ratio.HasValue && ratio.Value > 0) CtxRatio = ratio.Value;

            if (dur.HasValue && dur.Value > 0.05 && tok > 0)
            {
                LastReqS = dur;
                LastReqTps = tok / dur.Value;
                Samples.Add(new Sample(tok, dur.Value, real, g.End.Value, g.Model));
            }

            toolStart = g.End.HasValue ? g.End : nextTs;
            toolEnd = null;
        }

        void FlushTool(double? boundaryTs)
        {
            if (!toolStart.HasValue) return;
            double? end = toolEnd.HasValue ? toolEnd : boundaryTs;
            if (end.HasValue && end.Value >= toolStart.Value)
                ToolS += end.Value - toolStart.Value;
            toolStart = null;
            toolEnd = null;
        }

        public void Feed(Dictionary<string, object> o)
        {
            object typObj;
            o.TryGetValue("type", out typObj);
            string typ = QmUtil.Str(typObj) ?? "";
            object tsObj;
            o.TryGetValue("timestamp", out tsObj);
            double? ts = QmUtil.ParseTs(tsObj);

            if (typ == "runtime-config")
            {
                double? cw = GetNum(o, "contextWindow");
                if (cw.HasValue && cw.Value > 0) CtxWindow = (long)Math.Truncate(cw.Value);
                object mk;
                if (o.TryGetValue("model", out mk) && mk is string) ModelKey = (string)mk;
                return;
            }

            if (typ == "assistant")
            {
                Dictionary<string, object> m = GetDict(o, "message");
                if (m == null) m = new Dictionary<string, object>();
                if (cur == null)
                {
                    FlushTool(ts);
                    OpenGroup(ts);
                }
                if (ts.HasValue) cur.End = ts;
                foreach (object bo in QmUtil.BlocksOf(m))
                {
                    Dictionary<string, object> b = QmUtil.AsDict(bo);
                    if (b == null) continue;
                    object bt;
                    b.TryGetValue("type", out bt);
                    string t = QmUtil.Str(bt);
                    if (t == "text")
                        cur.CjkEst += QmUtil.EstimateTokens(GetStr(b, "text"));
                    else if (t == "thinking" || t == "redacted_thinking")
                        cur.CjkEst += QmUtil.EstimateTokens(GetStr(b, "thinking"));
                    else if (t == "tool_use")
                    {
                        object input;
                        if (!b.TryGetValue("input", out input) || input == null)
                            input = new Dictionary<string, object>();
                        string raw;
                        try { raw = QmJson.DumpPython(input); }
                        catch { raw = Convert.ToString(input, CultureInfo.InvariantCulture); }
                        cur.CjkEst += QmUtil.EstimateTokens(raw);
                    }
                }
                Dictionary<string, object> u = GetDict(m, "usage");
                if (u != null) cur.Usage = u;
                string id = GetStr(m, "id");
                if (!string.IsNullOrEmpty(id)) cur.ReqId = id;
                string mm = GetStr(m, "model");
                if (!string.IsNullOrEmpty(mm)) cur.Model = mm;
                if (ts.HasValue) lastTs = ts;
                return;
            }

            if (cur != null) CloseGroup(ts);

            if (typ == "user")
            {
                Dictionary<string, object> m = GetDict(o, "message");
                bool isToolResult = false;
                foreach (object bo in QmUtil.BlocksOf(m))
                {
                    Dictionary<string, object> b = QmUtil.AsDict(bo);
                    if (b != null)
                    {
                        object bt;
                        b.TryGetValue("type", out bt);
                        if (QmUtil.Str(bt) == "tool_result") { isToolResult = true; break; }
                    }
                }
                string pid = GetStr(o, "promptId");
                if (isToolResult)
                {
                    if (ts.HasValue && toolStart.HasValue)
                        toolEnd = toolEnd.HasValue ? Math.Max(toolEnd.Value, ts.Value) : ts.Value;
                }
                else if (!string.IsNullOrEmpty(pid))
                {
                    TurnIds.Add(pid);
                    FlushTool(ts);
                }
                object mk;
                if (o.TryGetValue("model", out mk) && mk is string) ModelKey = (string)mk;
            }

            if (ts.HasValue) lastTs = ts;
        }

        public bool ReadIncrement()
        {
            long size;
            try { size = new FileInfo(Path).Length; }
            catch { return false; }
            if (size < Offset) ResetState();
            if (size == Offset) return false;

            bool got = false;
            using (FileStream fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(Offset, SeekOrigin.Begin);
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8, true, 81920, true))
                {
                    string rawLine;
                    while ((rawLine = sr.ReadLine()) != null)
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0) continue;
                        Dictionary<string, object> o = QmJson.ParseObject(line);
                        if (o == null) { LinesBad++; continue; }
                        Feed(o);
                        got = true;
                    }
                }
                Offset = fs.Position;
            }
            return got;
        }

        public InflightInfo Inflight(double? now)
        {
            if (cur == null || !cur.Start.HasValue) return null;
            double t = now.HasValue ? now.Value : QmUtil.UnixNow();
            double dur = Math.Max(0.0, t - cur.Start.Value);
            Dictionary<string, object> u = cur.Usage ?? new Dictionary<string, object>();
            double? ot = GetNum(u, "output_tokens");
            double tok = (ot.HasValue && ot.Value > 0) ? ot.Value : cur.CjkEst;
            InflightInfo info = new InflightInfo();
            info.Dur = dur;
            info.Tokens = tok;
            info.Tps = (dur > 0.5 && tok > 0) ? (double?)(tok / dur) : null;
            info.Thinking = cur.CjkEst > 0;
            return info;
        }

        /// <summary>
        /// 活跃窗样本：以最后一条样本的结束时刻为锚点往前 ActiveWindowS 秒，再只保留与
        /// 窗内最后一个模型名相同的样本。锚点用样本时刻而不是墙钟，会话空闲时数字停在
        /// 最后一次活跃期的值上；同模型不足也照样只用同模型，宁可退回 (e2e) 或 —，
        /// 也不让切换前的模型稀释回归。
        /// </summary>
        public List<Sample> CurrentWindow()
        {
            List<Sample> pts = new List<Sample>();
            foreach (Sample s in Samples)
                if (s.Tok >= 20 && s.Dur >= 0.3 && s.Dur <= 600.0) pts.Add(s);
            if (pts.Count == 0) return pts;
            double anchor = double.MinValue;
            foreach (Sample s in pts) if (s.End > anchor) anchor = s.End;
            List<Sample> win = new List<Sample>();
            foreach (Sample s in pts) if (s.End >= anchor - ActiveWindowS) win.Add(s);

            string cur = null;
            for (int i = win.Count - 1; i >= 0 && cur == null; i--)
                if (!string.IsNullOrEmpty(win[i].Model)) cur = win[i].Model;
            if (cur == null) return win;
            List<Sample> same = new List<Sample>();
            foreach (Sample s in win) if (s.Model == cur) same.Add(s);
            return same.Count > 0 ? same : win;
        }

        /// <summary>窗内端到端速率（Σtokens/Σtime，含排队与首字），供回归不成立时回退。</summary>
        public bool WindowE2E(out double tps, out bool measured)
        {
            List<Sample> win = CurrentWindow();
            double tok = 0, dur = 0;
            foreach (Sample s in win) if (s.Real) { tok += s.Tok; dur += s.Dur; }
            if (tok <= 0 || dur <= 0)
            {
                tok = 0; dur = 0;
                foreach (Sample s in win) if (!s.Real) { tok += s.Tok; dur += s.Dur; }
            }
            tps = dur > 0 ? tok / dur : 0;
            measured = tok > 0 && dur > 0 && ContainsReal(win);
            return tps > 0;
        }

        static bool ContainsReal(List<Sample> win)
        {
            foreach (Sample s in win) if (s.Real) return true;
            return false;
        }

        public DecodeResult DecodeTps()
        {
            List<Sample> win = CurrentWindow();
            List<Sample> real = new List<Sample>();
            foreach (Sample s in win) if (s.Real) real.Add(s);
            List<Sample> use = real.Count >= DecodeMinSamples ? real : win;
            bool allMeasured = real.Count >= DecodeMinSamples;
            int n = use.Count;
            if (n < DecodeMinSamples) return new DecodeResult(false, 0, n, false);
            double mx = 0, my = 0;
            foreach (Sample s in use) { mx += s.Tok; my += s.Dur; }
            mx /= n; my /= n;
            double sxx = 0, sxy = 0, syy = 0;
            foreach (Sample s in use)
            {
                sxx += (s.Tok - mx) * (s.Tok - mx);
                sxy += (s.Tok - mx) * (s.Dur - my);
                syy += (s.Dur - my) * (s.Dur - my);
            }
            if (sxx <= 0 || sxy <= 0 || syy <= 0) return new DecodeResult(false, 0, n, false);
            if (sxy / Math.Sqrt(sxx * syy) < 0.25) return new DecodeResult(false, 0, n, false);
            double dec = 1.0 / (sxy / sxx);
            if (dec < 1.0 || dec > 500.0) return new DecodeResult(false, 0, n, false);
            return new DecodeResult(true, dec, n, allMeasured);
        }

        static double? GetNum(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(key, out o)) return null;
            return QmUtil.Num(o);
        }

        static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(key, out o)) return null;
            return QmUtil.AsDict(o);
        }

        static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(key, out o)) return null;
            return QmUtil.Str(o);
        }
    }

    // ---------------------------------------------------------------- 指标

    public class Metrics
    {
        public double ModelS, ToolS;
        public int Turns, Steps;
        public double? TotalTokens;
        public double? Tps;
        public bool TpsMeasured;
        public string TpsKind = "";
        public double? CtxPct;
        public long? CtxWindow;
        public double? CachePct;
        public double? Credits;
        public string Model = "?";
    }

    public static class MetricCalculator
    {
        public static Metrics Compute(SessionStats st, Dictionary<string, object> payload)
        {
            if (payload == null) payload = new Dictionary<string, object>();
            Dictionary<string, object> cw = GetDict(payload, "context_window");
            Dictionary<string, object> cost = GetDict(payload, "cost");

            double? usedPct = GetNum(cw, "used_percentage");
            double? cwSize = GetNum(cw, "context_window_size");
            long? ctxWindow = (cwSize.HasValue && cwSize.Value > 0)
                ? (long?)Math.Truncate(cwSize.Value) : null;
            if (!usedPct.HasValue && st != null && st.CtxRatio.HasValue)
                usedPct = st.CtxRatio.Value * 100.0;
            if (!ctxWindow.HasValue && st != null) ctxWindow = st.CtxWindow;

            double? credits = GetNum(cost, "total_credits");
            if (!credits.HasValue && st != null) credits = st.Credits;

            string model = null;
            Dictionary<string, object> pm = GetDict(payload, "model");
            if (pm != null)
            {
                model = GetStr(pm, "display_name");
                if (string.IsNullOrEmpty(model)) model = GetStr(pm, "id");
            }
            if (string.IsNullOrEmpty(model) && st != null) model = st.ModelKey;
            if (string.IsNullOrEmpty(model)) model = "?";

            Metrics m = new Metrics();
            m.Model = model;
            m.Credits = credits;
            m.CtxWindow = ctxWindow;
            m.CtxPct = usedPct;
            if (st != null)
            {
                m.ModelS = st.ModelS;
                m.ToolS = st.ToolS;
                m.Turns = st.TurnIds.Count;
                m.Steps = st.Steps;

                DecodeResult dec = st.DecodeTps();
                if (dec.HasValue)
                {
                    m.Tps = dec.Tps;
                    m.TpsMeasured = dec.AllMeasured;
                    m.TpsKind = dec.AllMeasured ? "" : "(估)";
                }
                else
                {
                    // 端到端回退也只在活跃窗+同模型的样本集上算，避免整场累计把旧模型的样本带进来。
                    double e2e;
                    bool meas;
                    if (st.WindowE2E(out e2e, out meas))
                    {
                        m.Tps = e2e;
                        m.TpsMeasured = meas;
                    }
                    m.TpsKind = "(e2e)";
                }

                if (st.InTokensTrue > 0 && st.CacheReadTrue > 0)
                {
                    if (st.CacheReadTrue > st.InTokensTrue)
                        m.CachePct = st.CacheReadTrue / (st.InTokensTrue + st.CacheReadTrue) * 100.0;
                    else
                        m.CachePct = st.CacheReadTrue / st.InTokensTrue * 100.0;
                }

                if (st.InTokensTrue > 0)
                    m.TotalTokens = st.InTokensTrue + st.MeasTok + st.EstTok;
            }
            return m;
        }

        public static string BuildLine(SessionStats st, Dictionary<string, object> payload, bool compact)
        {
            Metrics m = Compute(st, payload);
            List<string> segs = new List<string>();
            if (st != null)
            {
                if (!compact)
                {
                    segs.Add("模型 " + QmUtil.FmtDur(m.ModelS));
                    segs.Add("工具 " + QmUtil.FmtDur(m.ToolS));
                }
                segs.Add(m.Turns.ToString(CultureInfo.InvariantCulture) + "轮 "
                    + m.Steps.ToString(CultureInfo.InvariantCulture) + "步");
            }
            if (m.Tps.HasValue)
                segs.Add(Math.Round(m.Tps.Value, MidpointRounding.ToEven)
                    .ToString("0", CultureInfo.InvariantCulture) + " tok/s" + m.TpsKind);
            else
                segs.Add("tok/s —");
            if (m.TotalTokens.HasValue)
                segs.Add(QmUtil.FmtTokens(m.TotalTokens) + " tok");
            if (m.CtxPct.HasValue)
            {
                string tail = "";
                if (!compact && m.CtxWindow.HasValue)
                {
                    if (m.CtxWindow.Value >= 1000000)
                        tail = " (" + (m.CtxWindow.Value / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M)";
                    else
                        tail = " (" + (m.CtxWindow.Value / 1000).ToString("0", CultureInfo.InvariantCulture) + "k)";
                }
                segs.Add("上下文 " + m.CtxPct.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" + tail);
            }
            if (m.CachePct.HasValue)
                segs.Add("缓存命中 " + Math.Round(m.CachePct.Value, MidpointRounding.ToEven)
                    .ToString("0", CultureInfo.InvariantCulture) + "%");
            if (m.Credits.HasValue)
            {
                segs.Add("credits " + (compact
                    ? m.Credits.Value.ToString("0.0", CultureInfo.InvariantCulture)
                    : m.Credits.Value.ToString("0.00", CultureInfo.InvariantCulture)));
            }
            if (!compact) segs.Add(m.Model);
            return string.Join(" · ", segs.ToArray());
        }

        static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(key, out o)) return null;
            return QmUtil.AsDict(o);
        }
        static double? GetNum(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(key, out o)) return null;
            return QmUtil.Num(o);
        }
        static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object o;
            if (!d.TryGetValue(key, out o)) return null;
            return QmUtil.Str(o);
        }
    }

    // ---------------------------------------------------------------- 会话发现

    public class Finding
    {
        public double Mtime;
        public string Path;
        public Finding(double mtime, string path) { Mtime = mtime; Path = path; }
    }

    public static class SessionFinder
    {
        public static Finding FindActiveTranscript()
        {
            Finding best = null;
            double bestMt = 0;
            foreach (string p in Enumerate())
            {
                try
                {
                    FileInfo fi = new FileInfo(p);
                    if (fi.Length < 200) continue;
                    double mt = (fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    if (mt > bestMt) { bestMt = mt; best = new Finding(mt, p); }
                }
                catch { }
            }
            return best;
        }

        public static List<Finding> FindActiveTranscripts(int limit, double recentMin)
        {
            double now = QmUtil.UnixNow();
            List<Finding> rows = new List<Finding>();
            foreach (string p in Enumerate())
            {
                try
                {
                    FileInfo fi = new FileInfo(p);
                    if (fi.Length < 500) continue;
                    double mt = (fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                    if (now - mt > recentMin * 60.0) continue;
                    rows.Add(new Finding(mt, p));
                }
                catch { }
            }
            rows.Sort(delegate(Finding a, Finding b) { return b.Mtime.CompareTo(a.Mtime); });
            if (rows.Count > limit) rows = rows.GetRange(0, limit);
            return rows;
        }

        static IEnumerable<string> Enumerate()
        {
            if (!Directory.Exists(QmPaths.ProjectsDir)) yield break;
            string[] dirs;
            try { dirs = Directory.GetDirectories(QmPaths.ProjectsDir); }
            catch { yield break; }
            foreach (string d in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(d, "*.jsonl"); }
                catch { continue; }
                foreach (string f in files) yield return f;
            }
        }
    }

    // ---------------------------------------------------------------- 输出行

    public static class RowFactory
    {
        public static List<Dictionary<string, object>> Fresh(List<Finding> top, double now)
        {
            List<Dictionary<string, object>> outRows = new List<Dictionary<string, object>>();
            foreach (Finding f in top)
            {
                SessionStats st = new SessionStats(f.Path);
                st.ReadIncrement();
                outRows.Add(Build(st, f, now));
            }
            return outRows;
        }

        public static Dictionary<string, object> Build(SessionStats st, Finding f, double now)
        {
            Metrics m = MetricCalculator.Compute(st, null);
            string sid = Path.GetFileNameWithoutExtension(f.Path);
            string fallback = sid.Length > 4 ? sid.Substring(0, 4) : sid;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["session_id"] = sid;
            d["title"] = TitleStore.Get(sid, fallback);
            d["busy"] = st.Inflight(now) != null || (now - f.Mtime) < 25.0;
            d["turns"] = m.Turns;
            d["steps"] = m.Steps;
            d["tps"] = m.Tps.HasValue ? (object)Math.Round(m.Tps.Value, 1, MidpointRounding.ToEven) : null;
            d["tps_kind"] = m.TpsKind;
            d["total_tokens"] = m.TotalTokens.HasValue ? (object)Math.Round(m.TotalTokens.Value, MidpointRounding.ToEven) : null;
            d["ctx_pct"] = m.CtxPct.HasValue ? (object)Math.Round(m.CtxPct.Value, 1, MidpointRounding.ToEven) : null;
            d["cache_pct"] = m.CachePct.HasValue ? (object)Math.Round(m.CachePct.Value, 1, MidpointRounding.ToEven) : null;
            d["credits"] = m.Credits.HasValue ? (object)Math.Round(m.Credits.Value, 1, MidpointRounding.ToEven) : null;
            return d;
        }
    }

    public class SessionCache
    {
        readonly Dictionary<string, SessionStats> cache = new Dictionary<string, SessionStats>();
        readonly Dictionary<string, double> mtMap = new Dictionary<string, double>();

        public List<Dictionary<string, object>> Build(List<Finding> top, double now)
        {
            List<Dictionary<string, object>> outRows = new List<Dictionary<string, object>>();
            foreach (Finding f in top)
            {
                SessionStats st;
                if (!cache.TryGetValue(f.Path, out st))
                {
                    st = new SessionStats(f.Path);
                    cache[f.Path] = st;
                    mtMap[f.Path] = f.Mtime;
                    st.ReadIncrement();
                }
                else if (!mtMap.ContainsKey(f.Path) || mtMap[f.Path] != f.Mtime)
                {
                    mtMap[f.Path] = f.Mtime;
                    st.ReadIncrement();
                }
                outRows.Add(RowFactory.Build(st, f, now));
            }
            return outRows;
        }
    }
}
