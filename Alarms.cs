using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CubeScope
{
    /// <summary>One thing to watch (a DNIS, a dial-peer, a far-end address or every call): alarm when more than
    /// ThresholdPercent of its calls fail on a CUBE.</summary>
    public sealed class AlarmRule
    {
        public bool Enabled = true;
        /// <summary>The number, dial-peer or address watched; empty for Field "cube".</summary>
        public string Dnis = "";
        /// <summary>called (first number sent to) | dialed (as it arrived) | connected (transferred to it) | anyleg (called or connected)
        /// | dialpeer (a leg matched this dial-peer) | endpoint (a leg's far-end media address) | cube (every call on the CUBE)</summary>
        public string Field = "called";
        public string Match = "exact";                   // exact | suffix | prefix
        public List<string> Cubes = new List<string>();  // empty: every CUBE CubeScope reads
        public double ThresholdPercent = 1;
        public int WindowMinutes = 60;
        public int MinCalls = 50;
        public int MinFailures = 2;
        public List<string> FailResults = new List<string>(new string[] { "rejected", "failed" });
        public string Note = "";                         // added to this DNIS's alarm and clear (trap .13, the event's last line)
        /// <summary>A call canceled while ringing counts as failed only when the cancel came at least this
        /// many seconds after the call was set up (0: every cancel). Measured from setup, since the
        /// accounting records do not say when ringing began; it drops the callers who hang up at once.</summary>
        public double CancelMinSeconds = 0;

        /// <summary>connected and anyleg judge the leg sent to the number, not the whole call.</summary>
        public bool ByLeg { get { return Field == "connected" || Field == "anyleg"; } }

        /// <summary>What the rule watches, in the words of an alarm.</summary>
        public string Target
        {
            get
            {
                if (Field == "cube") return AllCalls;
                if (Field == "dialpeer") return "dial-peer " + Dnis;
                if (Field == "endpoint") return "far end " + (Match == "suffix" ? "*" : "") + Dnis + (Match == "prefix" ? "*" : "");
                return "DNIS " + Dnis;
            }
        }

        public const string AllCalls = "all calls";

        /// <summary>
        /// Names the rule in alarm keys, the trap's DNIS varbind and the status: the DNIS or dial-peer itself as before
        /// (so open alarms and an NMS keep working), "endpoint 68.68.122.*" for an address, "all calls" for a CUBE.
        /// </summary>
        public string Key
        {
            get
            {
                if (Field == "cube") return AllCalls;
                if (Field == "endpoint") return "endpoint " + (Match == "suffix" ? "*" : "") + Dnis + (Match == "prefix" ? "*" : "");
                return Dnis;
            }
        }

        /// <summary>A rule's Key in the words of an alarm (for an alarm whose rule is gone).</summary>
        public static string TargetOfKey(string key)
        {
            if (key == AllCalls) return AllCalls;
            if (key.StartsWith("endpoint ", StringComparison.Ordinal)) return "far end " + key.Substring(9);
            return "DNIS " + key;
        }

        /// <summary>Rules that pick their calls with the server filter alone, judged by the whole call.</summary>
        bool WholeCall { get { return Field == "dialpeer" || Field == "endpoint" || Field == "cube"; } }

        /// <summary>
        /// Whether this rule counts a call, and how it came out for this number. called and dialed go by
        /// the whole call. connected and anyleg go by the outbound legs to the number, because a transfer
        /// nobody answered sits on a call the customer answered, which reads as answered overall: any of
        /// those legs answering is good, else the last one tried decides, and a cancel's length is that
        /// leg's. connected leaves out calls first sent to the number. anyleg keeps a call the CUBE refused
        /// before placing a leg (its called number is this one, no leg went out), judged as a call.
        /// </summary>
        public bool Judge(Call c, out Result result, out int cause, out long lengthMs)
        {
            result = c.Result; cause = c.FinalCause; lengthMs = c.DurMs;
            if (WholeCall) return true;                          // ServerFilter is exact for these
            if (!ByLeg) return Hit(Field == "dialed" && !string.IsNullOrEmpty(c.Dialed) ? c.Dialed : c.Called);
            bool first = Hit(c.Called);
            if (first && Field == "connected") return false;
            bool answered;
            Leg to = c.LegTo(Hit, out answered);
            if (to == null) return first;
            result = answered ? Result.Answered : Store.ResultOf(to.Cause);
            cause = answered ? -1 : to.Cause;
            lengthMs = to.Disconnect - to.Setup;
            return true;
        }

        /// <summary>Whether an outcome counts against this rule, by its result (and, when canceled, how long it ran).</summary>
        public bool IsFailure(Result result, long lengthMs)
        {
            if (!FailResults.Contains(Names.Results[(int)result])) return false;
            return result != Result.Abandoned || CancelMinSeconds <= 0 || lengthMs >= (long)(CancelMinSeconds * 1000);
        }

        /// <summary>The dashboard filters that list this rule's failed calls (two when cancels need a length).</summary>
        public string FailedFilterText()
        {
            if (ByLeg) return Clause("legfail") + (Field == "connected" ? " !" + Clause("called") : "");
            List<string> other = new List<string>();
            foreach (string r in FailResults) if (r != "abandoned" || CancelMinSeconds <= 0) other.Add(r);
            string ft = FilterText(), lead = ft.Length > 0 ? ft + " " : "";
            string s = other.Count > 0 ? lead + "result=" + string.Join(",", other.ToArray()) : "";
            if (CancelMinSeconds > 0 && FailResults.Contains("abandoned"))
                s += (s.Length > 0 ? "  and  " : "") + lead + "result=abandoned dur>=" + CancelMinSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return s;
        }

        public const int NoteMax = 500;

        /// <summary>The message with the rule's note as its last line, when there is one.</summary>
        public string WithNote(string message) { return Note.Length > 0 ? message + "\nNote: " + Note : message; }

        /// <summary>What to type in the filter box for the same calls.</summary>
        public string FilterText()
        {
            if (Field == "cube") return "";
            if (Field == "endpoint") return Clause("mediaip");
            if (Field == "anyleg") return Clause("peer");
            if (Field == "connected") return Clause("peer") + " !" + Clause("called");
            return Clause(Field);
        }

        string Clause(string field)
        {
            if (Match == "suffix") return field + "=*" + Dnis;
            if (Match == "prefix") return field + "=" + Dnis;
            return field + "=\"" + Dnis + "\"";
        }

        /// <summary>A slightly wide server filter; Matches() makes the exact choice.</summary>
        public string ServerFilter()
        {
            if (Field == "dialpeer") return "dialpeer=" + Dnis;
            if (Field == "cube") return "";
            if (Field == "endpoint") return Clause("mediaip");
            string f = Field == "called" ? "called=" : "any=";
            return f + (Match == "prefix" ? Dnis : "*" + Dnis);
        }

        bool Hit(string num)
        {
            if (string.IsNullOrEmpty(num)) return false;
            num = num.TrimStart('+');
            if (Match == "suffix") return num.EndsWith(Dnis, StringComparison.Ordinal);
            if (Match == "prefix") return num.StartsWith(Dnis, StringComparison.Ordinal);
            return num == Dnis;
        }

        public void WriteJson(Json j)
        {
            j.Obj().P("enabled", Enabled).P("dnis", Dnis).P("field", Field).P("match", Match);
            j.Key("cubes").Arr(); foreach (string c in Cubes) j.Val(c); j.EndArr();
            j.P("thresholdPercent", ThresholdPercent).P("windowMinutes", WindowMinutes).P("minCalls", MinCalls).P("minFailures", MinFailures);
            j.Key("failResults").Arr(); foreach (string r in FailResults) j.Val(r); j.EndArr();
            j.P("note", Note);
            j.P("cancelMinSeconds", CancelMinSeconds);
            j.EndObj();
        }
    }

    /// <summary>The alarm settings, kept in alarms.json in the data folder and edited in the page.</summary>
    public sealed class AlarmConfig
    {
        public bool Enabled;                              // off until turned on in the settings window
        public int IntervalMinutes = 5;
        public bool EventLog = true;
        public string EventSource = "CubeScope DNIS Watch";
        public string AlertLevel = "Error";              // Error | Warning
        public List<string> TrapDestinations = new List<string>();
        public int TrapPort = 162;
        public string TrapCommunity = "public";
        public string TrapOid = "1.3.6.1.4.1.9999.1.3000";
        public int StaleMinutes = 0;                     // alarm when a CUBE sends no call records this long; 0 = off
        public int KeepDays = 0;                         // delete call record files older than this; 0 = keep them
        public int KeepSyslogDays = 0;                   // delete hourly syslog files older than this; 0 = keep them
        public List<AlarmRule> Rules = new List<AlarmRule>();

        public static readonly string[] KnownResults = { "answered", "abandoned", "noanswer", "busy", "rejected", "failed", "unknown" };

        public void WriteJson(Json j)
        {
            j.Obj().P("enabled", Enabled).P("intervalMinutes", IntervalMinutes).P("eventLog", EventLog).P("eventSource", EventSource)
             .P("alertLevel", AlertLevel);
            j.Key("trapDestinations").Arr(); foreach (string d in TrapDestinations) j.Val(d); j.EndArr();
            j.P("trapPort", TrapPort).P("trapCommunity", TrapCommunity).P("trapOid", TrapOid).P("staleMinutes", StaleMinutes).P("keepDays", KeepDays).P("keepSyslogDays", KeepSyslogDays);
            j.Key("rules").Arr(); foreach (AlarmRule r in Rules) r.WriteJson(j); j.EndArr();
            j.EndObj();
        }

        public string ToJsonText() { Json j = new Json(4096); WriteJson(j); return j.ToString(); }

        /// <summary>Reads and checks settings; null with a plain-English error when something is wrong.</summary>
        public static AlarmConfig FromJson(Dictionary<string, object> o, out string error)
        {
            error = null;
            AlarmConfig c = new AlarmConfig();
            if (o == null) { error = "no settings"; return null; }
            c.Enabled = JsonRead.GetBool(o, "enabled", false);
            c.IntervalMinutes = (int)JsonRead.GetNumber(o, "intervalMinutes", 5);
            c.EventLog = JsonRead.GetBool(o, "eventLog", true);
            c.EventSource = JsonRead.GetString(o, "eventSource", c.EventSource).Trim();
            c.AlertLevel = JsonRead.GetString(o, "alertLevel", "Error");
            c.TrapDestinations = JsonRead.GetStrings(o, "trapDestinations");
            c.TrapPort = (int)JsonRead.GetNumber(o, "trapPort", 162);
            c.TrapCommunity = JsonRead.GetString(o, "trapCommunity", "public");
            c.TrapOid = JsonRead.GetString(o, "trapOid", c.TrapOid).Trim();
            c.StaleMinutes = (int)JsonRead.GetNumber(o, "staleMinutes", 0);
            c.KeepDays = (int)JsonRead.GetNumber(o, "keepDays", 0);
            c.KeepSyslogDays = (int)JsonRead.GetNumber(o, "keepSyslogDays", 0);
            if (c.IntervalMinutes < 1 || c.IntervalMinutes > 60) { error = "check every 1 to 60 minutes"; return null; }
            if (c.EventSource.Length == 0) { error = "the event source needs a name"; return null; }
            if (c.AlertLevel != "Error" && c.AlertLevel != "Warning") { error = "the alarm event level is Error or Warning"; return null; }
            if (c.TrapPort < 1 || c.TrapPort > 65535) { error = "the trap port is 1 to 65535"; return null; }
            if (!Snmp.ValidOid(c.TrapOid)) { error = "the trap OID should look like 1.3.6.1.4.1.9999.1.3000"; return null; }
            if (c.TrapDestinations.Count > 0 && string.IsNullOrEmpty(c.TrapCommunity)) { error = "traps need a community"; return null; }
            foreach (string d in c.TrapDestinations)
                if (d.IndexOf(' ') >= 0 || d.Length > 255) { error = "'" + d + "' is not an address"; return null; }
            if (c.StaleMinutes < 0 || c.StaleMinutes > 1440) { error = "the no-records warning is 0 (off) to 1440 minutes"; return null; }
            if (c.KeepDays < 0 || c.KeepDays > 3650) { error = "keep call record files for 0 (keep them all) to 3650 days"; return null; }
            if (c.KeepSyslogDays < 0 || c.KeepSyslogDays > 3650) { error = "keep syslog files for 0 (keep them all) to 3650 days"; return null; }

            object rv;
            List<object> rules = o.TryGetValue("rules", out rv) ? rv as List<object> : null;
            if (rules != null)
            {
                if (rules.Count > 50) { error = "at most 50 rules"; return null; }
                Dictionary<string, bool> seen = new Dictionary<string, bool>();
                for (int i = 0; i < rules.Count; i++)
                {
                    Dictionary<string, object> ro = rules[i] as Dictionary<string, object>;
                    if (ro == null) { error = "rule " + (i + 1) + " is not a rule"; return null; }
                    AlarmRule r = new AlarmRule();
                    r.Enabled = JsonRead.GetBool(ro, "enabled", true);
                    r.Dnis = JsonRead.GetString(ro, "dnis", "").Trim().TrimStart('+');
                    r.Field = JsonRead.GetString(ro, "field", "called").ToLowerInvariant();
                    r.Match = JsonRead.GetString(ro, "match", "exact").ToLowerInvariant();
                    r.Cubes = JsonRead.GetStrings(ro, "cubes");
                    r.ThresholdPercent = JsonRead.GetNumber(ro, "thresholdPercent", 1);
                    r.WindowMinutes = (int)JsonRead.GetNumber(ro, "windowMinutes", 60);
                    r.MinCalls = (int)JsonRead.GetNumber(ro, "minCalls", 50);
                    r.MinFailures = (int)JsonRead.GetNumber(ro, "minFailures", 2);
                    // one line of plain text: line breaks become spaces, other control characters go
                    StringBuilder nb = new StringBuilder();
                    foreach (char ch in JsonRead.GetString(ro, "note", "")) nb.Append(ch == '\r' || ch == '\n' || ch == '\t' ? ' ' : char.IsControl(ch) ? '\0' : ch);
                    r.Note = nb.ToString().Replace("\0", "").Trim();
                    if (r.Note.Length > AlarmRule.NoteMax) { error = "rule " + (i + 1) + ": the note is at most " + AlarmRule.NoteMax + " characters"; return null; }
                    List<string> fr = JsonRead.GetStrings(ro, "failResults");
                    if (fr.Count > 0)
                    {
                        r.FailResults = new List<string>();
                        foreach (string w0 in fr)
                        {
                            string w = w0.ToLowerInvariant();
                            if (w == "canceled" || w == "cancelled") w = "abandoned";
                            if (Array.IndexOf(KnownResults, w) < 0) { error = "rule " + (i + 1) + ": '" + w0 + "' is not a result"; return null; }
                            if (!r.FailResults.Contains(w)) r.FailResults.Add(w);
                        }
                    }
                    string where = "rule " + (i + 1) + ": ";
                    if (r.Field != "called" && r.Field != "dialed" && r.Field != "dialpeer" && r.Field != "endpoint" && r.Field != "cube" && !r.ByLeg)
                    { error = where + "it watches called, dialed, connected, anyleg, dialpeer, endpoint or cube"; return null; }
                    if (r.Field == "cube") { r.Dnis = ""; r.Match = "exact"; }
                    else if (r.Field == "endpoint")
                    {
                        if (r.Dnis.Length == 0) { error = where + "the far-end address is empty"; return null; }
                        foreach (char ch in r.Dnis)
                            if (!(char.IsDigit(ch) || ch == '.' || ch == ':' || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                            { error = where + "the far-end address should look like 192.0.2.10, or its start such as 192.0.2."; return null; }
                    }
                    else
                    {
                        if (r.Dnis.Length == 0) { error = where + "the DNIS is empty"; return null; }
                        foreach (char ch in r.Dnis) if (!(char.IsDigit(ch) || ch == '*' || ch == '#')) { error = where + "the DNIS should be digits"; return null; }
                    }
                    if (r.Field == "dialpeer")
                    {
                        int peerTag;
                        if (!int.TryParse(r.Dnis, NumberStyles.None, CultureInfo.InvariantCulture, out peerTag) || peerTag < 1) { error = where + "a dial-peer is a whole number"; return null; }
                        r.Match = "exact";
                    }
                    if (r.Match != "exact" && r.Match != "suffix" && r.Match != "prefix") { error = where + "match is exact, suffix or prefix"; return null; }
                    if (!(r.ThresholdPercent >= 0 && r.ThresholdPercent <= 100)) { error = where + "the limit is 0 to 100 percent"; return null; }
                    if (r.WindowMinutes < 5 || r.WindowMinutes > 1440) { error = where + "the window is 5 to 1440 minutes"; return null; }
                    if (r.MinCalls < 1 || r.MinCalls > 1000000) { error = where + "the minimum calls is 1 or more"; return null; }
                    if (r.MinFailures < 1 || r.MinFailures > 1000000) { error = where + "the minimum failures is 1 or more"; return null; }
                    if (r.FailResults.Count == 0) { error = where + "count at least one result as a failure"; return null; }
                    r.CancelMinSeconds = JsonRead.GetNumber(ro, "cancelMinSeconds", 0);
                    if (!(r.CancelMinSeconds >= 0 && r.CancelMinSeconds <= 600)) { error = where + "a cancel's minimum length is 0 to 600 seconds"; return null; }
                    if (seen.ContainsKey(r.Key)) { error = where + r.Target + " is already watched by another rule"; return null; }
                    seen[r.Key] = true;
                    c.Rules.Add(r);
                }
            }
            return c;
        }
    }

    /// <summary>What an alarm event or trap is about (the trap's varbinds).</summary>
    public sealed class AlarmInfo
    {
        public string Cube = "", Dnis = "", Pct = "", Threshold = "", Key = "", Note = "";
        public long Calls, Failed;
    }

    /// <summary>One alarm, clear, warning or test that was sent, for the window's history.</summary>
    public sealed class AlarmHistoryEntry
    {
        public string Time = "", State = "", Level = "", Cube = "", Dnis = "", Text = "", EventLog = "", Traps = "";
        public int Id;

        public void WriteJson(Json j)
        {
            j.Obj().P("time", Time).P("id", Id).P("state", State).P("level", Level).P("cube", Cube).P("dnis", Dnis)
             .P("text", Text).P("eventLog", EventLog).P("traps", Traps).EndObj();
        }

        public static AlarmHistoryEntry From(Dictionary<string, object> o)
        {
            AlarmHistoryEntry e = new AlarmHistoryEntry();
            e.Time = JsonRead.GetString(o, "time", ""); e.Id = (int)JsonRead.GetNumber(o, "id", 0); e.State = JsonRead.GetString(o, "state", "");
            e.Level = JsonRead.GetString(o, "level", ""); e.Cube = JsonRead.GetString(o, "cube", ""); e.Dnis = JsonRead.GetString(o, "dnis", "");
            e.Text = JsonRead.GetString(o, "text", ""); e.EventLog = JsonRead.GetString(o, "eventLog", ""); e.Traps = JsonRead.GetString(o, "traps", "");
            return e;
        }
    }

    /// <summary>The last judgement of one rule on one CUBE, for the settings window.</summary>
    public sealed class AlarmStatus
    {
        public string Dnis, Cube, State, Text, Since;
        /// <summary>What the rule watches, in words ("DNIS 5550200", "far end 192.0.2.*", "all calls").</summary>
        public string Target;
        public int Calls, Failed;
        public double Pct;
    }

    /// <summary>
    /// Judges every rule on every CUBE each IntervalMinutes against the store the service is serving,
    /// and raises an event and/or an SNMP trap exactly when a condition changes: 1001 once when a
    /// CUBE goes over the limit for a DNIS, 1002 once when it is back (or the rule is removed, or alarms
    /// are turned off), 1003/1004 for cannot-check and checking again, 1000 for the test. The same ids,
    /// trap OIDs and varbinds as tools\Watch-DnisFailures.ps1, so an NMS set up for one serves both.
    /// Open alarms are kept in alarm-state.json, so a restart neither repeats nor forgets them.
    /// </summary>
    public sealed class AlarmEngine
    {
        public readonly string Dir;
        readonly Func<Store> _store;
        readonly object _lock = new object();
        AlarmConfig _cfg = new AlarmConfig();
        Dictionary<string, string> _open = new Dictionary<string, string>();   // key -> since (UTC, ISO)
        List<AlarmStatus> _status = new List<AlarmStatus>();
        List<string> _problems = new List<string>();
        readonly List<AlarmHistoryEntry> _history = new List<AlarmHistoryEntry>();   // oldest first
        public const int HistoryKeep = 1000;
        DateTime _lastRun = DateTime.MinValue;
        string _note = "not run yet";
        Timer _timer;
        int _running;
        static readonly DateTime Started = DateTime.UtcNow;

        public string Zone = "UTC";                                  // the window is on this clock (tests: cube)
        public string EventFile;                                     // testing: events to this file, not the event log
        public Func<DateTime> UtcNow = delegate () { return DateTime.UtcNow; };
        public Func<int, string, string, AlarmInfo, bool> Deliver;   // tests replace it
        public Action<string> OnLog = delegate (string s) { Log.Info(s); };

        public const string CannotKey = "__cannot-check__";
        public static readonly Dictionary<int, string> States = new Dictionary<int, string>
        {
            { 1000, "notice" }, { 1001, "alarm" }, { 1002, "clear" }, { 1003, "cannot-check" }, { 1004, "checking-again" },
            { 1005, "alarm" }, { 1006, "clear" }
        };
        /// <summary>In an alarm's key and trap, where a DNIS would be: this CUBE's call records have stopped.</summary>
        public const string RecordsDnis = "records";
        /// <summary>The folder of call record files to clean up; the host sets it.</summary>
        public string CdrRoot;
        /// <summary>What is being read, so syslog cleanup touches only those files; the host sets it.</summary>
        public LoadOptions SyslogFeed;
        DateTime _lastCleanup = DateTime.MinValue;
        int _lastKeepDays, _lastKeepSyslogDays;
        volatile string _cleanupNote = "";
        /// <summary>Sources a call record has ever been read from, kept across restarts: only these can be said to have stopped.</summary>
        HashSet<string> _recordsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> _neverSent = new List<string>();

        public AlarmEngine(string dir, Func<Store> store)
        {
            Dir = dir;
            _store = store;
            Deliver = DeliverReal;
        }

        public string ConfigFile { get { return Path.Combine(Dir, "alarms.json"); } }
        public string StateFile { get { return Path.Combine(Dir, "alarm-state.json"); } }
        public string HistoryFile { get { return Path.Combine(Dir, "alarm-history.jsonl"); } }
        public string RecordsSeenFile { get { return Path.Combine(Dir, "records-seen.json"); } }
        public AlarmConfig Config { get { lock (_lock) return _cfg; } }
        /// <summary>What the last clean-up of old files did, in words; "" before the first or when nothing is to be deleted.</summary>
        public string CleanupNote { get { return _cleanupNote; } }

        // ------------------------------------------------------------ settings and state on disk

        public void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(ConfigFile))
                    {
                        string err;
                        AlarmConfig c = AlarmConfig.FromJson(JsonRead.Parse(File.ReadAllText(ConfigFile, Encoding.UTF8)) as Dictionary<string, object>, out err);
                        if (c != null) _cfg = c;
                        else Log.Warn("alarm settings in " + ConfigFile + " not used: " + err);
                    }
                }
                catch (Exception ex) { Log.Error("cannot read " + ConfigFile, ex); }
                _open = new Dictionary<string, string>();
                try
                {
                    if (File.Exists(StateFile))
                    {
                        Dictionary<string, object> s = JsonRead.Parse(File.ReadAllText(StateFile, Encoding.UTF8)) as Dictionary<string, object>;
                        if (s != null) foreach (KeyValuePair<string, object> kv in s) _open[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception ex) { Log.Error("cannot read " + StateFile + "; starting with no open alarms", ex); }
                _recordsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(RecordsSeenFile))
                    {
                        List<object> names = JsonRead.Parse(File.ReadAllText(RecordsSeenFile, Encoding.UTF8)) as List<object>;
                        if (names != null) foreach (object n in names) _recordsSeen.Add(Convert.ToString(n, CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex) { Log.Error("cannot read " + RecordsSeenFile, ex); }
                _history.Clear();
                try
                {
                    if (File.Exists(HistoryFile))
                        foreach (string line in File.ReadAllLines(HistoryFile, Encoding.UTF8))
                        {
                            if (line.Trim().Length == 0) continue;
                            try { _history.Add(AlarmHistoryEntry.From(JsonRead.Parse(line) as Dictionary<string, object>)); }
                            catch (FormatException) { }
                        }
                    if (_history.Count > HistoryKeep) _history.RemoveRange(0, _history.Count - HistoryKeep);
                }
                catch (Exception ex) { Log.Error("cannot read " + HistoryFile, ex); }
            }
        }

        /// <summary>
        /// Adds what was sent to the history: kept in memory (the newest HistoryKeep) and appended to
        /// alarm-history.jsonl, one line each; the file is rewritten from memory once it is twice that long.
        /// </summary>
        void Remember(int id, string level, string message, AlarmInfo info, string eventLog, string traps)
        {
            AlarmHistoryEntry e = new AlarmHistoryEntry();
            e.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            e.Id = id; e.State = States.ContainsKey(id) ? States[id] : ""; e.Level = level;
            e.Cube = info != null ? info.Cube : ""; e.Dnis = info != null ? info.Dnis : "";
            e.Text = message.Split('\n')[0].TrimEnd('\r'); e.EventLog = eventLog; e.Traps = traps;
            lock (_history)
            {
                _history.Add(e);
                bool rewrite = _history.Count > 2 * HistoryKeep;
                if (_history.Count > HistoryKeep && rewrite) _history.RemoveRange(0, _history.Count - HistoryKeep);
                try
                {
                    Directory.CreateDirectory(Dir);
                    Json j = new Json(512);
                    if (rewrite)
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (AlarmHistoryEntry h in _history) { Json hj = new Json(512); h.WriteJson(hj); sb.Append(hj.ToString()).Append("\r\n"); }
                        WriteAtomically(HistoryFile, sb.ToString());
                    }
                    else { e.WriteJson(j); File.AppendAllText(HistoryFile, j.ToString() + "\r\n", new UTF8Encoding(false)); }
                }
                catch (Exception ex) { Log.Error("cannot write " + HistoryFile, ex); }
            }
        }

        public List<AlarmHistoryEntry> History(int newest)
        {
            lock (_history)
            {
                List<AlarmHistoryEntry> r = new List<AlarmHistoryEntry>();
                for (int i = _history.Count - 1; i >= 0 && r.Count < newest; i--) r.Add(_history[i]);
                return r;
            }
        }

        internal static void WriteAtomically(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        void SaveState()
        {
            Json j = new Json(1024);
            j.Obj();
            foreach (KeyValuePair<string, string> kv in _open) j.P(kv.Key, kv.Value);
            j.EndObj();
            try { WriteAtomically(StateFile, j.ToString()); }
            catch (Exception ex) { Log.Error("cannot write " + StateFile, ex); }
        }

        /// <summary>Saves new settings (already checked) and runs a check with them at once.</summary>
        public void Save(AlarmConfig c)
        {
            lock (_lock)
            {
                WriteAtomically(ConfigFile, c.ToJsonText());
                _cfg = c;
                OnLog("alarm settings saved: " + (c.Enabled ? "on" : "off") + ", every " + c.IntervalMinutes + " min, " +
                      c.Rules.Count + " rule(s), event log " + (c.EventLog ? "on" : "off") + ", trap destinations " +
                      (c.TrapDestinations.Count > 0 ? string.Join(",", c.TrapDestinations.ToArray()) : "none") +
                      ", keep call record files " + (c.KeepDays > 0 ? c.KeepDays + " days" : "all") +
                      ", keep syslog files " + (c.KeepSyslogDays > 0 ? c.KeepSyslogDays + " days" : "all"));
            }
            Restart();
            ThreadPool.QueueUserWorkItem(delegate (object o) { RunOnce(); });
        }

        /// <summary>Uses these settings without saving them or touching the timer (the self-test).</summary>
        public void Use(AlarmConfig c) { lock (_lock) _cfg = c; }

        // ------------------------------------------------------------ timer

        public void Start() { Restart(); }

        void Restart()
        {
            int ms = Config.IntervalMinutes * 60000;
            lock (_lock)
            {
                if (_timer != null) _timer.Dispose();
                _timer = new Timer(delegate (object o) { RunOnce(); }, null, Math.Min(ms, 60000), ms);
            }
        }

        public void Stop()
        {
            lock (_lock) { if (_timer != null) { _timer.Dispose(); _timer = null; } }
        }

        /// <summary>One check now, unless one is already running.</summary>
        public void RunOnce()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            try { Evaluate(UtcNow()); }
            catch (Exception ex) { Log.Error("alarm check failed", ex); }
            try { Cleanup(DateTime.UtcNow); }
            catch (Exception ex) { Log.Error("cleaning up old files failed", ex); }
            finally { Interlocked.Exchange(ref _running, 0); }
        }

        /// <summary>Deletes call record and syslog files past their days, at most once an hour (or at once when a setting changes).</summary>
        public void Cleanup(DateTime nowUtc)
        {
            AlarmConfig c = Config;
            int cdrDays = string.IsNullOrEmpty(CdrRoot) ? 0 : c.KeepDays;
            int logDays = SyslogFeed == null ? 0 : c.KeepSyslogDays;
            bool changed = cdrDays != _lastKeepDays || logDays != _lastKeepSyslogDays;
            _lastKeepDays = cdrDays; _lastKeepSyslogDays = logDays;
            if (cdrDays < 1 && logDays < 1) { _cleanupNote = ""; return; }
            if (!changed && _lastCleanup != DateTime.MinValue && (nowUtc - _lastCleanup).TotalMinutes < 55) return;
            _lastCleanup = nowUtc;
            List<string> said = new List<string>();
            bool worthLogging = false;
            long bytes; int failed, n;
            if (cdrDays > 0)
            {
                n = CdrRetention.Run(CdrRoot, cdrDays, nowUtc, out bytes, out failed);
                said.Add(CleanupText("call record", n, cdrDays, bytes, failed));
                worthLogging |= n > 0 || failed > 0;
            }
            if (logDays > 0)
            {
                n = SyslogRetention.Run(SyslogFeed, logDays, nowUtc, out bytes, out failed);
                said.Add(CleanupText("syslog", n, logDays, bytes, failed));
                worthLogging |= n > 0 || failed > 0;
            }
            _cleanupNote = "Cleanup at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ": " + string.Join("; ", said.ToArray());
            if (worthLogging) OnLog(_cleanupNote);
        }

        static string CleanupText(string kind, int n, int days, long bytes, int failed)
        {
            return (n == 0 ? "no " + kind + " files were older than " + days + " days" :
                    "deleted " + n.ToString("N0", CultureInfo.InvariantCulture) + " " + kind + (n == 1 ? " file" : " files") + " older than " + days + " days (" + MemoryGuard.MB(bytes) + ")") +
                   (failed > 0 ? ", " + failed + " could not be deleted" : "");
        }

        // ------------------------------------------------------------ the judgement

        static string Lasted(string since, DateTime now)
        {
            DateTime s;
            if (!DateTime.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out s))
                return "an unknown time";
            int m = (int)(now - s).TotalMinutes;
            if (m < 1) return "under a minute";
            if (m < 60) return m + " min";
            return (m / 60) + " h " + (m % 60) + " min";
        }

        static string P2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture) + "%"; }
        static string Num(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        public void Evaluate(DateTime nowUtc)
        {
            lock (_lock)
            {
                AlarmConfig cfg = _cfg;
                string windowEnd = nowUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
                string via = "CubeScope on " + Environment.MachineName;
                _lastRun = DateTime.Now;
                List<AlarmStatus> status = new List<AlarmStatus>();
                List<string> problems = new List<string>();
                Dictionary<string, bool> live = new Dictionary<string, bool>();

                if (!cfg.Enabled)
                {
                    _note = "alarms are off";
                    CloseAll("alarms were turned off", nowUtc, via);
                    _status = status; _problems = problems;
                    SaveState();
                    return;
                }
                Store st = _store();
                if (st == null) { _note = "still reading the logs"; _status = status; return; }

                long toMs = TimeUtil.FromDate(nowUtc);
                foreach (AlarmRule rule in cfg.Rules)
                {
                    if (!rule.Enabled) continue;
                    long fromMs = toMs - rule.WindowMinutes * 60000L;
                    string windowText = "the " + rule.WindowMinutes + " minutes to " + windowEnd;
                    Filter f = Filter.Parse(rule.ServerFilter());
                    List<string> cubes = new List<string>();
                    Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    lock (st.Sync)
                    {
                        for (int i = 0; i < st.Sources.Count; i++)
                        {
                            index[st.Sources[i].Name] = i;
                            foreach (string other in st.Sources[i].AlsoNames) index[other] = i;      // a rule can name it either way
                        }
                        if (rule.Cubes.Count > 0) cubes.AddRange(rule.Cubes);
                        else for (int i = 0; i < st.Sources.Count; i++) cubes.Add(st.Sources[i].Name);
                    }
                    foreach (string cube in cubes)
                    {
                        int si;
                        if (!index.TryGetValue(cube, out si))
                        {
                            List<string> names = new List<string>(index.Keys);
                            names.Sort(StringComparer.OrdinalIgnoreCase);
                            problems.Add(rule.Target + ": CubeScope reads no CUBE named '" + cube + "' (it reads: " + string.Join(", ", names.ToArray()) + ")");
                            AlarmStatus ms = new AlarmStatus(); ms.Dnis = rule.Key; ms.Target = rule.Target; ms.Cube = cube; ms.State = "missing"; ms.Text = "no such CUBE in the logs";
                            status.Add(ms);
                            continue;
                        }
                        string cname = st.Sources[si].Name;
                        int calls = 0, fails = 0;
                        Dictionary<string, int> byResult = new Dictionary<string, int>();
                        Dictionary<string, int> byCause = new Dictionary<string, int>();
                        lock (st.Sync)
                        {
                            st.DeriveDirty();
                            QueryParams q = new QueryParams();
                            q.Src = si; q.Zone = Zone; q.From = fromMs; q.To = toMs; q.Bucket = 3600; q.Filter = f;
                            QueryResult r = Query.Run(st, q);
                            foreach (Call c in r.Matched)
                            {
                                Result outcome; int cause; long length;
                                if (!rule.Judge(c, out outcome, out cause, out length)) continue;
                                calls++;
                                if (!rule.IsFailure(outcome, length)) continue;
                                string res = Names.Results[(int)outcome];
                                fails++;
                                int n; byResult.TryGetValue(res, out n); byResult[res] = n + 1;
                                string ck = cause < 0 ? "(no cause)" : cause + " " + Q850.Name(cause);
                                byCause.TryGetValue(ck, out n); byCause[ck] = n + 1;
                            }
                        }

                        double pct = calls > 0 ? 100.0 * fails / calls : 0;
                        bool enough = calls >= rule.MinCalls;
                        bool over = enough && fails >= rule.MinFailures && pct > rule.ThresholdPercent;
                        string key = cname + "|" + rule.Key;
                        live[key] = true;
                        AlarmStatus s = new AlarmStatus();
                        s.Dnis = rule.Key; s.Target = rule.Target; s.Cube = cname; s.Calls = calls; s.Failed = fails; s.Pct = pct;
                        s.State = over ? "alarm" : !enough ? "too-few" : "ok";
                        s.Text = fails + " of " + calls + " calls failed (" + P2(pct) + ")" +
                                 (over ? ", over the " + Num(rule.ThresholdPercent) + "% limit" : !enough ? ", too few calls to judge (needs " + rule.MinCalls + ")" :
                                  pct > rule.ThresholdPercent ? ", over " + Num(rule.ThresholdPercent) + "% but an alarm needs " + rule.MinFailures + " failures" : ", under the limit");
                        OnLog(cname + " " + rule.Target + ": " + s.Text + " in " + windowText);

                        AlarmInfo info = new AlarmInfo();
                        info.Cube = cname; info.Dnis = rule.Key; info.Calls = calls; info.Failed = fails;
                        info.Pct = pct.ToString("0.00", CultureInfo.InvariantCulture); info.Threshold = Num(rule.ThresholdPercent); info.Key = key; info.Note = rule.Note;
                        string since;
                        bool open = _open.TryGetValue(key, out since);
                        if (over && !open)
                        {
                            List<KeyValuePair<string, int>> rl = new List<KeyValuePair<string, int>>(byResult);
                            rl.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                            List<KeyValuePair<string, int>> cl = new List<KeyValuePair<string, int>>(byCause);
                            cl.Sort(delegate (KeyValuePair<string, int> a, KeyValuePair<string, int> b) { return b.Value.CompareTo(a.Value); });
                            StringBuilder res = new StringBuilder(), cau = new StringBuilder();
                            foreach (KeyValuePair<string, int> kv in rl)
                                res.Append(res.Length > 0 ? ", " : "").Append(kv.Key == "abandoned" ? "canceled while ringing" : kv.Key).Append(' ').Append(kv.Value)
                                   .Append(kv.Key == "abandoned" && rule.CancelMinSeconds > 0 ? " (after " + Num(rule.CancelMinSeconds) + " s or more)" : "");
                            for (int i = 0; i < cl.Count && i < 5; i++) cau.Append(i > 0 ? "; " : "").Append(cl[i].Key).Append(": ").Append(cl[i].Value);
                            string msg = "Greater than " + Num(rule.ThresholdPercent) + "% call failures to " + rule.Target + " on " + cname + "\n" +
                                         fails + " of " + calls + " calls failed (" + P2(pct) + ") in " + windowText + ".\n" +
                                         "Failed by result: " + res + ".\nCauses: " + cau + ".\n" +
                                         (rule.ByLeg ? (rule.Field == "connected" ? "Counts calls transferred to this number" : "Counts every call with a leg to this number") +
                                                       ", judged by that leg. The filter lists every such leg nobody answered.\n" : "") +
                                         (rule.Field == "endpoint" ? "Counts every call with a leg whose far end sent audio from this address (from the call record files).\n" :
                                          rule.Field == "cube" ? "Counts every call on this CUBE.\n" :
                                          rule.Field == "dialpeer" ? "Counts every call with a leg on this dial-peer (from the call record files).\n" : "") +
                                         "In CubeScope: CUBE " + cname + ", filter  " + rule.FailedFilterText() + "\n" +
                                         "Read by " + via + ".";
                            msg = rule.WithNote(msg);
                            if (Deliver(1001, cfg.AlertLevel, msg, info)) { since = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); _open[key] = since; }
                        }
                        else if (!over && open)
                        {
                            string why = "Back under the limit: " + fails + " of " + calls + " calls failed (" + P2(pct) + ")";
                            if (!enough) why = "Too few calls to judge now: " + fails + " of " + calls + " failed (needs " + rule.MinCalls + " calls)";
                            else if (pct > rule.ThresholdPercent) why = "Over " + Num(rule.ThresholdPercent) + "% but only " + fails + " failure(s) of " + calls + " calls (" + P2(pct) + "); an alarm needs " + rule.MinFailures;
                            string msg = "Cleared: no longer greater than " + Num(rule.ThresholdPercent) + "% call failures to " + rule.Target + " on " + cname + "\n" +
                                         why + " in " + windowText + ". The alarm lasted " + Lasted(since, DateTime.UtcNow) + ". Read by " + via + ".";
                            msg = rule.WithNote(msg);
                            if (Deliver(1002, "Information", msg, info)) { _open.Remove(key); since = null; }
                        }
                        if (_open.TryGetValue(key, out since)) s.Since = since;
                        status.Add(s);
                    }
                }
                if (cfg.StaleMinutes > 0) WatchRecords(st, cfg, toMs, nowUtc, via, status, live);
                else _neverSent.Clear();

                // alarms whose rule (or CUBE) is gone get their clear, so the NMS is not left holding them
                List<string> gone = new List<string>();
                foreach (string k in _open.Keys) if (k != CannotKey && !live.ContainsKey(k)) gone.Add(k);
                foreach (string k in gone)
                {
                    string[] kp = k.Split('|');
                    AlarmInfo gi = new AlarmInfo(); gi.Cube = kp[0]; gi.Dnis = kp.Length > 1 ? kp[1] : ""; gi.Key = k;
                    bool records = gi.Dnis == RecordsDnis;
                    string msg = (records ? "Cleared: no longer watching the call records of " + gi.Cube + "\nThe warning was turned off, or this CUBE is no longer read."
                                          : "Cleared: no longer watching call failures to " + AlarmRule.TargetOfKey(gi.Dnis) + " on " + gi.Cube + "\nThe alarm rule was removed or turned off.") +
                                 " The alarm lasted " + Lasted(_open[k], DateTime.UtcNow) + ". Read by " + via + ".";
                    if (Deliver(records ? 1006 : 1002, "Information", msg, gi)) _open.Remove(k);
                }

                bool cannotOpen = _open.ContainsKey(CannotKey);
                AlarmInfo ci = new AlarmInfo(); ci.Key = "cannot-check";
                if (problems.Count > 0 && !cannotOpen)
                {
                    if (Deliver(1003, "Warning", "CubeScope alarms cannot check everything\n" + string.Join("\n", problems.ToArray()), ci))
                        _open[CannotKey] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                }
                else if (problems.Count == 0 && cannotOpen)
                {
                    if (Deliver(1004, "Information", "CubeScope alarms are checking everything again. They could not for " + Lasted(_open[CannotKey], DateTime.UtcNow) + ".", ci))
                        _open.Remove(CannotKey);
                }
                foreach (string p in problems) OnLog("cannot check: " + p);

                _status = status; _problems = problems;
                _note = "checked " + status.Count + " DNIS/CUBE pair(s)" + NeverSentNote();
                SaveState();
            }
        }

        /// <summary>
        /// Per CUBE, with or without DNIS rules: 1005 once when its newest call record is older than the limit,
        /// 1006 once when records are back. Silence is how a CUBE's accounting fails: a CUBE that loses its SFTP
        /// server writes to flash from then on, and stays there until someone resets it. A source no call record
        /// has ever been read from has not stopped: it is a stray log, or a router sharing the folder.
        /// </summary>
        void WatchRecords(Store st, AlarmConfig cfg, long toMs, DateTime nowUtc, string via, List<AlarmStatus> status, Dictionary<string, bool> live)
        {
            int count;
            lock (st.Sync) count = st.Sources.Count;
            bool seenChanged = false;
            _neverSent = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string name;
                long syslog, files;
                lock (st.Sync)
                {
                    name = st.Sources[i].Name;
                    ZoneMap zm = ZoneMap.Build(st, Zone);
                    syslog = zm.ToDisplay(i, st.Sources[i].LastSyslogRecordMs);
                    files = zm.ToDisplay(i, st.Sources[i].LastFileRecordMs);
                }
                if (syslog >= 0) seenChanged |= _recordsSeen.Add(name + "|syslog");
                if (files >= 0) seenChanged |= _recordsSeen.Add(name + "|files");
                bool watchSyslog = _recordsSeen.Contains(name + "|syslog"), watchFiles = _recordsSeen.Contains(name + "|files");
                if (!watchSyslog && !watchFiles) { _neverSent.Add(name); continue; }
                string key = name + "|" + RecordsDnis;
                live[key] = true;
                // each feed on its own: syslog still arriving must not hide an SFTP delivery that has failed
                long limit = toMs - cfg.StaleMinutes * 60000L;
                bool syslogStale = watchSyslog && (syslog < 0 || syslog < limit), filesStale = watchFiles && (files < 0 || files < limit);
                bool both = watchSyslog && watchFiles;
                long newest = both ? Math.Min(syslog, files) : watchSyslog ? syslog : files;
                bool stale = syslogStale || filesStale;
                long ago = newest < 0 ? -1 : Math.Max(0, (toMs - newest) / 60000);
                AlarmStatus s = new AlarmStatus();
                s.Dnis = "(call records)"; s.Target = "its call records"; s.Cube = name; s.State = stale ? "alarm" : "ok";
                s.Text = both ? "call record files " + FeedAge(files, toMs) + ", syslog " + FeedAge(syslog, toMs) + " (limit " + cfg.StaleMinutes + ")"
                         : newest < 0 ? "no call records in the logs loaded" : "newest call record is " + ago + " min old (limit " + cfg.StaleMinutes + ")";
                AlarmInfo info = new AlarmInfo();
                info.Cube = name; info.Dnis = RecordsDnis; info.Key = key; info.Threshold = cfg.StaleMinutes.ToString(CultureInfo.InvariantCulture);
                string since;
                bool open = _open.TryGetValue(key, out since);
                if (stale && !open)
                {
                    string msg = "No call records from " + name + " for " + cfg.StaleMinutes + " minutes or more\n" +
                                 (newest < 0 ? "None are in the logs loaded now." : "The newest is " + ago + " minutes old.") +
                                 (both && filesStale != syslogStale ? (filesStale ? " The call record files have stopped; syslog is still arriving." : " Syslog has stopped; the call record files are still arriving.") +
                                  " If that feed was stopped on purpose, delete records-seen.json in CubeScope's data folder." : "") + "\n" +
                                 "A CUBE sending files over SFTP may have fallen back to its flash. On the CUBE check: dir flash: | include cdr. " +
                                 "Fix the cause, then run: file-acct reset.\nA CUBE sending syslog: check it still reaches the syslog server.\nRead by " + via + ".";
                    if (Deliver(1005, cfg.AlertLevel, msg, info)) { since = nowUtc.ToString("o", CultureInfo.InvariantCulture); _open[key] = since; }
                }
                else if (!stale && open)
                {
                    string msg = "Cleared: call records from " + name + " are arriving again\nThe newest is " + ago + " minutes old. They stopped for " + Lasted(since, nowUtc) + ". Read by " + via + ".";
                    if (Deliver(1006, "Information", msg, info)) _open.Remove(key);
                }
                if (_open.TryGetValue(key, out since)) s.Since = since;
                status.Add(s);
            }
            if (seenChanged) SaveRecordsSeen();
        }

        static string FeedAge(long newest, long toMs)
        {
            return newest < 0 ? "none loaded" : Math.Max(0, (toMs - newest) / 60000) + " min old";
        }

        void SaveRecordsSeen()
        {
            Json j = new Json(1024);
            j.Arr();
            foreach (string n in _recordsSeen) j.Val(n);
            j.EndArr();
            try { WriteAtomically(RecordsSeenFile, j.ToString()); }
            catch (Exception ex) { Log.Error("cannot write " + RecordsSeenFile, ex); }
        }

        /// <summary>Says who is left out, so a real CUBE with accounting never set up is not silently unwatched.</summary>
        string NeverSentNote()
        {
            if (_neverSent.Count == 0) return "";
            int show = Math.Min(5, _neverSent.Count);
            return "; no call records ever read from " + string.Join(", ", _neverSent.GetRange(0, show).ToArray()) +
                   (_neverSent.Count > show ? " and " + (_neverSent.Count - show) + " more" : "") + ", so not watched for them";
        }

        void CloseAll(string reason, DateTime nowUtc, string via)
        {
            List<string> keys = new List<string>(_open.Keys);
            foreach (string k in keys)
            {
                AlarmInfo gi = new AlarmInfo(); gi.Key = k == CannotKey ? "cannot-check" : k;
                string msg;
                int id;
                if (k == CannotKey) { id = 1004; msg = "CubeScope alarms: " + reason + "; the cannot-check warning is closed."; }
                else
                {
                    string[] kp = k.Split('|'); gi.Cube = kp[0]; gi.Dnis = kp.Length > 1 ? kp[1] : "";
                    bool records = gi.Dnis == RecordsDnis;
                    id = records ? 1006 : 1002;
                    msg = (records ? "Cleared: no longer watching the call records of " + gi.Cube : "Cleared: no longer watching call failures to " + AlarmRule.TargetOfKey(gi.Dnis) + " on " + gi.Cube) + "\n" +
                          "CubeScope " + reason + ". The alarm lasted " + Lasted(_open[k], nowUtc) + ". Read by " + via + ".";
                }
                if (Deliver(id, "Information", msg, gi)) _open.Remove(k);
            }
        }

        // ------------------------------------------------------------ delivery: event log and traps

        public static byte[] BuildTrap(AlarmConfig cfg, int id, string message, AlarmInfo info, long upTimeHundredths, int requestId)
        {
            string b = cfg.TrapOid;
            string first = message.Split('\n')[0].TrimEnd('\r');
            List<Snmp.VarBind> v = new List<Snmp.VarBind>();
            v.Add(Snmp.VarBind.Str(b + ".1", first));
            v.Add(Snmp.VarBind.Gauge(b + ".2", id));
            v.Add(Snmp.VarBind.Str(b + ".3", States.ContainsKey(id) ? States[id] : ""));
            v.Add(Snmp.VarBind.Str(b + ".4", info.Cube));
            v.Add(Snmp.VarBind.Str(b + ".5", info.Dnis));
            v.Add(Snmp.VarBind.Gauge(b + ".6", info.Calls));
            v.Add(Snmp.VarBind.Gauge(b + ".7", info.Failed));
            v.Add(Snmp.VarBind.Str(b + ".8", info.Pct));
            v.Add(Snmp.VarBind.Str(b + ".9", info.Threshold));
            v.Add(Snmp.VarBind.Str(b + ".10", Environment.MachineName));
            v.Add(Snmp.VarBind.Str(b + ".11", info.Key));
            v.Add(Snmp.VarBind.Str(b + ".12", message.Replace("\r", "").Replace("\n", " | ")));
            v.Add(Snmp.VarBind.Str(b + ".13", info.Note ?? ""));
            return Snmp.V2cTrap(cfg.TrapCommunity, b + "." + id, upTimeHundredths, requestId, v);
        }

        static long UpTime() { return (long)((DateTime.UtcNow - Started).TotalMilliseconds / 10); }
        static readonly Random Rnd = new Random();
        static int RequestId() { lock (Rnd) return Rnd.Next(1, int.MaxValue); }

        bool WriteEvent(AlarmConfig cfg, int id, string level, string message)
        {
            try
            {
                if (EventFile != null)
                {
                    File.AppendAllText(EventFile, "=== " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                       " UTC  event " + id + "  " + level + "  (" + cfg.EventSource + ")\r\n" + message.Replace("\n", "\r\n") + "\r\n\r\n");
                }
                else
                {
                    try { if (!EventLog.SourceExists(cfg.EventSource)) EventLog.CreateEventSource(cfg.EventSource, "Application"); }
                    catch (Exception) { }   // only an administrator can look or register; WriteEntry says if it is missing
                    EventLogEntryType t = level == "Error" ? EventLogEntryType.Error : level == "Warning" ? EventLogEntryType.Warning : EventLogEntryType.Information;
                    EventLog.WriteEntry(cfg.EventSource, message.Replace("\n", "\r\n"), t, id, 0);
                }
                OnLog("event " + id + " " + level + " written");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("event " + id + " could not be written to the Application log (source '" + cfg.EventSource + "')", ex);
                return false;
            }
        }

        /// <summary>Event log and/or traps, as set up; true when it reached at least one (or nothing is set up).</summary>
        bool DeliverReal(int id, string level, string message, AlarmInfo info)
        {
            AlarmConfig cfg = Config;
            bool any = false, ok = false;
            string ev = "off", tr = "none set";
            if (cfg.EventLog || EventFile != null)
            {
                any = true;
                bool w = WriteEvent(cfg, id, level, message);
                ok |= w;
                ev = w ? "written" : "FAILED";
            }
            if (cfg.TrapDestinations.Count > 0)
            {
                any = true;
                int n = Snmp.Send(BuildTrap(cfg, id, message, info, UpTime(), RequestId()), cfg.TrapDestinations, cfg.TrapPort, Log.Warn);
                if (n > 0) { ok = true; OnLog("trap " + cfg.TrapOid + "." + id + " (" + States[id] + ") sent to " + n + " destination(s)"); }
                tr = (n == cfg.TrapDestinations.Count ? "" : n + " of " + cfg.TrapDestinations.Count + ": ") + string.Join(", ", cfg.TrapDestinations.ToArray()) +
                     (n == 0 ? " - FAILED" : "");
            }
            Remember(id, level, message, info, ev, tr);
            return ok || !any;
        }

        /// <summary>
        /// The test button: one Information event (1000) and, with traps set up, the pair the NMS will
        /// really see - a test alarm (1001) and, clearSeconds later, its clear (1002) - on CUBE TEST, so it
        /// can never close a real alarm. Uses the settings given (the window's, saved or not).
        /// </summary>
        public string Test(AlarmConfig cfg, int clearSeconds)
        {
            string dnis = cfg.Rules.Count > 0 ? cfg.Rules[0].Key : "0";
            string target = cfg.Rules.Count > 0 ? cfg.Rules[0].Target : "DNIS 0";
            string thr = cfg.Rules.Count > 0 ? Num(cfg.Rules[0].ThresholdPercent) : "1";
            AlarmRule first = cfg.Rules.Count > 0 ? cfg.Rules[0] : new AlarmRule();
            string when = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            List<string> said = new List<string>();
            if (cfg.EventLog || EventFile != null)
            {
                bool ok = WriteEvent(cfg, 1000, "Information", "CubeScope alarm test from " + Environment.MachineName + " at " + when + "." +
                                     (cfg.TrapDestinations.Count > 0 ? " A test alarm trap and, " + clearSeconds + " seconds later, its clear are sent to " +
                                      string.Join(", ", cfg.TrapDestinations.ToArray()) + "." : ""));
                said.Add(ok ? "Test event 1000 written to the Application log (source " + cfg.EventSource + ")." : "The test event could NOT be written to the Application log - see the log.");
                if (cfg.TrapDestinations.Count == 0)
                {
                    AlarmInfo ti = new AlarmInfo(); ti.Dnis = dnis;
                    Remember(1000, "Information", "Test from the Alarms window", ti, ok ? "written" : "FAILED", "none set");
                }
            }
            if (cfg.TrapDestinations.Count > 0)
            {
                AlarmInfo ai = new AlarmInfo(); ai.Cube = "TEST"; ai.Dnis = dnis; ai.Calls = 200; ai.Failed = 4; ai.Pct = "2.00"; ai.Threshold = thr; ai.Key = "TEST|" + dnis; ai.Note = first.Note;
                string a = first.WithNote("TEST - Greater than " + thr + "% call failures to " + target + " on TEST\nNot a real failure: a test from CubeScope on " +
                           Environment.MachineName + " at " + when + ". 4 of 200 calls failed (2.00%).");
                int n = Snmp.Send(BuildTrap(cfg, 1001, a, ai, UpTime(), RequestId()), cfg.TrapDestinations, cfg.TrapPort, Log.Warn);
                OnLog("test alarm trap " + cfg.TrapOid + ".1001 sent to " + n + " destination(s)");
                string evNote = cfg.EventLog || EventFile != null ? "event 1000 " + (said.Count > 0 && said[0].StartsWith("Test event", StringComparison.Ordinal) ? "written" : "FAILED") : "off";
                Remember(1001, "Information", a, ai, evNote, string.Join(", ", cfg.TrapDestinations.ToArray()) + (n == 0 ? " - FAILED" : ""));
                said.Add("Test alarm trap (" + cfg.TrapOid + ".1001) handed to the network for " + n + " of " + cfg.TrapDestinations.Count +
                         " destination(s); its clear (" + cfg.TrapOid + ".1002) follows in " + clearSeconds + " seconds.");
                AlarmConfig keep = cfg;
                Thread t = new Thread(delegate ()
                {
                    Thread.Sleep(clearSeconds * 1000);
                    AlarmInfo ci = new AlarmInfo(); ci.Cube = "TEST"; ci.Dnis = dnis; ci.Calls = 200; ci.Failed = 0; ci.Pct = "0.00"; ci.Threshold = thr; ci.Key = "TEST|" + dnis; ci.Note = first.Note;
                    string c = first.WithNote("TEST - Cleared: no longer greater than " + thr + "% call failures to " + target + " on TEST\nNot a real failure: a test from CubeScope on " +
                               Environment.MachineName + ". 0 of 200 calls failed (0.00%). The alarm lasted " + clearSeconds + " seconds.");
                    int n2 = Snmp.Send(BuildTrap(keep, 1002, c, ci, UpTime(), RequestId()), keep.TrapDestinations, keep.TrapPort, Log.Warn);
                    OnLog("test clear trap " + keep.TrapOid + ".1002 sent to " + n2 + " destination(s)");
                    Remember(1002, "Information", c, ci, "-", string.Join(", ", keep.TrapDestinations.ToArray()) + (n2 == 0 ? " - FAILED" : ""));
                });
                t.IsBackground = true;
                t.Start();
            }
            if (said.Count == 0) said.Add("Nothing to test: the Windows event log is off and there are no trap destinations.");
            return string.Join(" ", said.ToArray());
        }

        // ------------------------------------------------------------ for the page

        public void WriteStatusJson(Json j)
        {
            lock (_lock)
            {
                j.Obj();
                j.P("lastRun", _lastRun == DateTime.MinValue ? "" : _lastRun.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                j.P("note", _note);
                j.Key("checks").Arr();
                foreach (AlarmStatus s in _status)
                    j.Obj().P("dnis", s.Dnis).P("target", s.Target ?? ("DNIS " + s.Dnis)).P("cube", s.Cube).P("state", s.State).P("text", s.Text ?? "").P("calls", s.Calls).P("failed", s.Failed)
                     .P("pct", s.Pct).P("since", s.Since ?? "").EndObj();
                j.EndArr();
                j.Key("problems").Arr(); foreach (string p in _problems) j.Val(p); j.EndArr();
                j.P("open", _open.Count).P("cleanup", _cleanupNote);
                j.Key("history").Arr();
                foreach (AlarmHistoryEntry h in History(200)) h.WriteJson(j);
                j.EndArr();
                j.EndObj();
            }
        }
    }
}
