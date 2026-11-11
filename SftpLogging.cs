using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CubeScope
{
    /// <summary>
    /// The Admin page's "Detailed SFTP logging" switch, for troubleshooting uploads such as a CUBE's
    /// %VOICE_FILE_ACCT-3-DUMPFAIL. For a set time OpenSSH logs every connection step (sshd LogLevel DEBUG2) and every
    /// file-transfer request with its size and answer (internal-sftp -l DEBUG3). The lines go to Windows' own OpenSSH
    /// event logs, not to files: the file-transfer part runs as the CUBE's account, which may not write in
    /// C:\ProgramData\ssh\logs, while any process may write an event. Windows keeps OpenSSH's debug lines in the
    /// OpenSSH/Debug log, which is off until turned on and can only be read once turned off again.
    /// A Windows scheduled task turns it all off at the end, even with the page closed, and saves the window's lines
    /// to one text file. Everything is put back as it was. OpenSSH never writes passwords to its log.
    /// </summary>
    public static class SftpLogConfig
    {
        public const string Marker = "# CubeScope detailed SFTP logging";
        public const string SshdLevel = "DEBUG2", SftpLevel = "DEBUG3";
        const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        static readonly Regex MarkerLine = new Regex(@"^# CubeScope detailed SFTP logging until (\S+)", RegexOptions.CultureInvariant);
        static readonly Regex Detailed = new Regex(@"^(\s*ForceCommand\s+internal-sftp)\s+-l\s+" + SftpLevel + @"\s*$", Opt);
        static readonly Regex Plain = new Regex(@"^(\s*ForceCommand\s+internal-sftp)\s*$", Opt);
        static readonly Regex UserLine = new Regex(@"^\s*Match\s+User\s+""?([^\s""]+)""?\s*$", Opt);
        static readonly Regex MatchLine = new Regex(@"^\s*Match\s", Opt);
        static readonly Regex OurSetting = new Regex(@"^\s*(SyslogFacility|LogLevel)\s", Opt);

        static List<string> Split(string text, out string nl)
        {
            nl = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            return new List<string>(text.Replace("\r\n", "\n").Split('\n'));
        }

        public static bool IsOn(string text)
        {
            DateTime until;
            return IsOn(text, out until);
        }

        /// <summary>Whether the file has CubeScope's logging block, and until when (UTC) it was set.</summary>
        public static bool IsOn(string text, out DateTime untilUtc)
        {
            untilUtc = DateTime.MinValue;
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                Match m = MarkerLine.Match(line);
                if (!m.Success) continue;
                DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out untilUtc);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The file with detailed logging on until untilUtc: a marked block at the very top (the first value of a keyword
        /// wins, so it overrides whatever the file says further down, and taking it out puts that back), and -l DEBUG3 on the
        /// account's ForceCommand internal-sftp. Null when the account has no such line. Doing it again only moves the time.
        /// </summary>
        public static string On(string text, string account, DateTime untilUtc)
        {
            string nl;
            List<string> ls = Split(text, out nl);
            RemoveBlock(ls);
            bool found = false;
            for (int i = 0; i < ls.Count; i++)
            {
                Match u = UserLine.Match(ls[i]);
                if (!u.Success || !u.Groups[1].Value.Equals(account, StringComparison.OrdinalIgnoreCase)) continue;
                for (int j = i + 1; j < ls.Count && !MatchLine.IsMatch(ls[j]); j++)
                {
                    Match p = Plain.Match(ls[j]);
                    if (!p.Success) p = Detailed.Match(ls[j]);
                    if (!p.Success) continue;
                    ls[j] = p.Groups[1].Value + " -l " + SftpLevel;
                    found = true;
                }
            }
            if (!found) return null;
            ls.InsertRange(0, new[] {
                Marker + " until " + untilUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + " (CubeScope turns it off then)",
                "SyslogFacility AUTH",
                "LogLevel " + SshdLevel,
                "" });
            return string.Join(nl, ls);
        }

        /// <summary>Whether some ForceCommand still carries the file-transfer logging (a half-undone switch).</summary>
        public static bool HasDetailedLine(string text)
        {
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n')) if (Detailed.IsMatch(line)) return true;
            return false;
        }

        /// <summary>The file with CubeScope's logging taken out again: exactly what it was before On.</summary>
        public static string Off(string text)
        {
            string nl;
            List<string> ls = Split(text, out nl);
            RemoveBlock(ls);
            for (int i = 0; i < ls.Count; i++)
            {
                Match d = Detailed.Match(ls[i]);
                if (d.Success) ls[i] = d.Groups[1].Value;
            }
            return string.Join(nl, ls);
        }

        static void RemoveBlock(List<string> ls)
        {
            for (int i = 0; i < ls.Count; i++)
            {
                if (!ls[i].StartsWith(Marker, StringComparison.Ordinal)) continue;
                int end = i + 1;
                while (end < ls.Count && OurSetting.IsMatch(ls[end])) end++;
                if (end < ls.Count && ls[end].Trim().Length == 0) end++;
                ls.RemoveRange(i, end - i);
                i--;
            }
        }
    }

    /// <summary>A Windows event log's settings.</summary>
    public sealed class EventLogInfo
    {
        public bool Exists, Enabled;
        public long MaxBytes;
    }

    /// <summary>What the detailed-logging switch does to this computer. For each change: null when done, or the reason in words.</summary>
    public interface ISftpLogOps
    {
        DateTime UtcNow();
        /// <summary>Free bytes on the drive Windows keeps its event logs on; -1 when that cannot be told.</summary>
        long FreeBytes();
        EventLogInfo GetEventLog(string name);
        /// <summary>Turns a log on or off and, when maxBytes is above 0, sets how much it may hold.</summary>
        string SetEventLog(string name, bool enabled, long maxBytes);
        /// <summary>Writes the logs' lines between the two times, oldest first, to one text file in LoggingFolder.</summary>
        string ExportEvents(string[] names, DateTime fromUtc, DateTime toUtc, string fileName, out int count);
        /// <summary>A Windows scheduled task that runs exe --sftp-logging-off at that time, as SYSTEM.</summary>
        string ScheduleOff(DateTime atUtc, string exe);
        string CancelScheduledOff();
        /// <summary>Where the saved logs and the switch's notes are kept (made on first use, administrators and SYSTEM only).</summary>
        string LoggingFolder();
        /// <summary>The switch's notes (the event logs' settings before), or null when there are none.</summary>
        string LoggingState();
        /// <summary>Saves the notes; null deletes them.</summary>
        string SaveLoggingState(string json);
        List<ReceiverFile> LoggingFiles();
        /// <summary>The full path of a saved log, by its name; null when there is no such file.</summary>
        string LoggingFilePath(string name);
    }

    public sealed partial class ReceiverAdmin
    {
        public const string OperationalLog = "OpenSSH/Operational", DebugLog = "OpenSSH/Debug";
        public const long DebugBytes = 256L << 20, OperationalBytes = 64L << 20, MinFreeBytes = 1L << 30;
        static readonly Regex LogName = new Regex(@"^sftp-log-[0-9A-Za-z-]+\.txt$", RegexOptions.CultureInvariant);

        public static bool SafeLogName(string name) { return name != null && name.Length <= 80 && LogName.IsMatch(name); }

        static string MB(long b) { return b < 0 ? "?" : (b / (1024.0 * 1024)).ToString(b < 10L << 20 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB"; }

        /// <summary>A time on this server's clock, as the page shows it.</summary>
        static string Local(DateTime utc) { return utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture); }

        sealed class LogState
        {
            public DateTime Started;
            public bool DebugExisted, DebugWasOn;
            public long DebugWasMax, OperationalWasMax;

            public string ToJson()
            {
                Json j = new Json(256);
                j.Obj().P("started", Started.ToString("o", CultureInfo.InvariantCulture)).P("debugExisted", DebugExisted).P("debugWasOn", DebugWasOn)
                 .P("debugWasMax", DebugWasMax).P("operationalWasMax", OperationalWasMax).EndObj();
                return j.ToString();
            }

            public static LogState From(string text)
            {
                if (string.IsNullOrEmpty(text)) return null;
                try
                {
                    Dictionary<string, object> o = JsonRead.Parse(text) as Dictionary<string, object>;
                    if (o == null) return null;
                    LogState s = new LogState();
                    DateTime.TryParse(JsonRead.GetString(o, "started", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out s.Started);
                    if (s.Started.Kind == DateTimeKind.Local) s.Started = s.Started.ToUniversalTime();
                    s.DebugExisted = JsonRead.GetBool(o, "debugExisted", false);
                    s.DebugWasOn = JsonRead.GetBool(o, "debugWasOn", false);
                    s.DebugWasMax = (long)JsonRead.GetNumber(o, "debugWasMax", 0);
                    s.OperationalWasMax = (long)JsonRead.GetNumber(o, "operationalWasMax", 0);
                    return s;
                }
                catch (FormatException) { return null; }
            }
        }

        /// <summary>Turns detailed logging on for minutes (5 to 120), or moves its end when it is on already.</summary>
        public ReceiverReview LoggingOn(int minutes, out bool busy)
        {
            busy = Interlocked.CompareExchange(ref _busy, 1, 0) != 0;
            if (busy) return null;
            try
            {
                ReceiverReview r = new ReceiverReview();
                IReceiverOps o = Ops;
                if (minutes < 5 || minutes > 120) { r.Blocked.Add("Choose 5 to 120 minutes."); return r; }
                string config = o.ConfigText(), account, folder;
                if (o.SshdExe() == null || config == null || !SshdConfig.FindBlock(config, out account, out folder))
                {
                    r.Blocked.Add("Set the receiver up first (Review changes, then Apply): OpenSSH has no file-transfer account from CubeScope yet.");
                    return r;
                }
                DateTime now = o.UtcNow(), until = now.AddMinutes(minutes), was;
                bool already = SftpLogConfig.IsOn(config, out was);
                if (SftpLogConfig.On(config, account, until) == null)
                {
                    r.Blocked.Add("OpenSSH's settings give " + account + " no \"ForceCommand internal-sftp\" line to add the logging to. Press Review changes, then Apply, first.");
                    return r;
                }
                long free = o.FreeBytes();
                if (!already && free >= 0 && free < MinFreeBytes)
                    r.Blocked.Add("Only " + MB(free) + " is free on the drive Windows keeps its event logs on. Detailed logging can take a few hundred MB: free at least 1 GB first.");
                if (r.Blocked.Count > 0) return r;

                EventLogInfo dbg = o.GetEventLog(DebugLog), op = o.GetEventLog(OperationalLog);
                int port = SshdConfig.ActivePort(config);
                if (port == 0) port = 22;
                string acct = account;
                Step(r, (already ? "Move the end of the detailed logging to " : "Set OpenSSH to log every connection step and every file-transfer request until ") + Local(until) +
                        " (this server's clock). The current settings are copied first.", true, delegate ()
                {
                    string cur = o.ConfigText();
                    if (cur == null) return "OpenSSH's settings file is missing.";
                    string next = SftpLogConfig.On(cur, acct, until);
                    if (next == null) return "OpenSSH's settings no longer have the account's file-transfer line.";
                    string backup;
                    string e = o.WriteConfig(next, out backup);
                    if (e != null) return e;
                    string t = o.TestConfig(o.SshdExe());
                    if (t == null) return null;
                    string back = o.RestoreConfig(backup);
                    return "OpenSSH rejected the logging settings, so nothing else was changed: " + t +
                           (back == null ? " The old settings were put back." : " Putting the old settings back also failed: " + back + " The copy is " + backup);
                });
                Step(r, "Note how OpenSSH's event logs are set now, to put them back afterwards", !already || o.LoggingState() == null, delegate ()
                {
                    LogState s = new LogState();
                    s.Started = now; s.DebugExisted = dbg.Exists; s.DebugWasOn = dbg.Enabled; s.DebugWasMax = dbg.MaxBytes; s.OperationalWasMax = op.MaxBytes;
                    return o.SaveLoggingState(s.ToJson());
                });
                if (dbg.Exists)
                    Step(r, "Turn on OpenSSH's debug log (" + DebugLog + "), holding up to " + MB(DebugBytes), !(dbg.Enabled && dbg.MaxBytes >= DebugBytes),
                         delegate () { return o.SetEventLog(DebugLog, true, DebugBytes); });
                else
                    r.Notes.Add("This server has no " + DebugLog + " event log, so only the lines OpenSSH writes to " + OperationalLog + " are kept: connections and file opens, closes and errors, without each request.");
                Step(r, op.Exists ? "Let OpenSSH's Operational log hold up to " + MB(OperationalBytes) + " (now " + MB(op.MaxBytes) + ")" : OperationalLog + " is not on this server",
                     op.Exists && op.MaxBytes < OperationalBytes, delegate () { return o.SetEventLog(OperationalLog, true, OperationalBytes); });
                Step(r, "Turn it off by itself at " + Local(until) + ", even with this page closed, and save the log: a Windows scheduled task runs " +
                        o.ExePath() + " --sftp-logging-off then, so leave that file where it is", true, delegate () { return o.ScheduleOff(until, o.ExePath()); });
                Step(r, "Restart OpenSSH so it logs in detail (an upload running at that moment is cut off; the CUBE sends it again)", !already, delegate () { return o.Restart(); });
                string portText = port.ToString(CultureInfo.InvariantCulture);
                Step(r, "Check that OpenSSH answers on port " + portText, !already, delegate ()
                {
                    for (int i = 0; i < 20; i++)
                    {
                        string b = o.PortBanner(port);
                        if (b != null && b.StartsWith("SSH-", StringComparison.Ordinal)) return null;
                        o.Wait(500);
                    }
                    return "OpenSSH is not answering on port " + portText + ".";
                });
                Log.Info("admin: detailed SFTP logging " + (already ? "moved to end at " : "on until ") + until.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
                RunSteps(r, false);
                return r;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>
        /// Puts everything back and saves the session's lines: OpenSSH's settings and a restart, the debug log off (it can
        /// only be read then), the lines to a file, the logs' sizes, the scheduled task. Each step is tried even when one
        /// before it failed. why goes in the troubleshooting log ("turned off on the Admin page", "the time set for it is up").
        /// </summary>
        public ReceiverReview LoggingOff(string why, out bool busy)
        {
            busy = Interlocked.CompareExchange(ref _busy, 1, 0) != 0;
            if (busy) return null;
            try
            {
                ReceiverReview r = new ReceiverReview();
                IReceiverOps o = Ops;
                string config = o.ConfigText();
                bool on = config != null && (SftpLogConfig.IsOn(config) || SftpLogConfig.HasDetailedLine(config));
                LogState s = LogState.From(o.LoggingState());
                EventLogInfo dbg = o.GetEventLog(DebugLog);
                DateTime now = o.UtcNow();
                DateTime from = s != null && s.Started > DateTime.MinValue ? s.Started.AddMinutes(-1) : now.AddHours(-2);
                if (!on && s == null) { r.Notes.Add("Detailed SFTP logging is already off."); }
                int port = config != null ? SshdConfig.ActivePort(config) : 22;
                if (port == 0) port = 22;
                string portText = port.ToString(CultureInfo.InvariantCulture);
                string fileName = "sftp-log-" + from.AddMinutes(s != null ? 1 : 0).ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-to-" +
                                  now.ToLocalTime().ToString("HHmm", CultureInfo.InvariantCulture) + ".txt";

                Step(r, "Put OpenSSH's usual log settings back", on, delegate ()
                {
                    string cur = o.ConfigText();
                    if (cur == null) return "OpenSSH's settings file is missing.";
                    string next = SftpLogConfig.Off(cur);
                    if (next == cur) return null;
                    string backup;
                    string e = o.WriteConfig(next, out backup);
                    if (e != null) return e;
                    string t = o.TestConfig(o.SshdExe());
                    if (t == null) return null;
                    string back = o.RestoreConfig(backup);
                    return "OpenSSH rejected its own settings without the logging: " + t + (back == null ? " They were put back as they were." : " Putting them back failed: " + back + " The copy is " + backup);
                });
                Step(r, "Restart OpenSSH", on, delegate () { return o.Restart(); });
                Step(r, "Check that OpenSSH answers on port " + portText, on, delegate ()
                {
                    for (int i = 0; i < 20; i++)
                    {
                        string b = o.PortBanner(port);
                        if (b != null && b.StartsWith("SSH-", StringComparison.Ordinal)) return null;
                        o.Wait(500);
                    }
                    return "OpenSSH is not answering on port " + portText + ".";
                });
                bool debugWasOn = s != null && s.DebugWasOn;
                if (dbg.Exists)
                    Step(r, "Turn OpenSSH's debug log off (Windows can only read it then)", dbg.Enabled, delegate () { return o.SetEventLog(DebugLog, false, 0); });
                ReceiverStep save = Step(r, "Save this session's OpenSSH lines to " + Path.Combine(o.LoggingFolder(), fileName), on || s != null, null);
                save.Run = delegate ()
                {
                    int n;
                    string e = o.ExportEvents(new[] { OperationalLog, DebugLog }, from, now, fileName, out n);
                    save.Detail = n.ToString("N0", CultureInfo.InvariantCulture) + " lines";
                    return e;
                };
                if (s != null && s.DebugExisted && s.DebugWasMax > 0)
                    Step(r, "Put the debug log back as it was (" + (debugWasOn ? "on" : "off") + ", " + MB(s.DebugWasMax) + ")", debugWasOn || s.DebugWasMax != DebugBytes,
                         delegate () { return o.SetEventLog(DebugLog, debugWasOn, s.DebugWasMax); });
                if (s != null && s.OperationalWasMax > 0)
                    Step(r, "Put the Operational log's size back (" + MB(s.OperationalWasMax) + ")", s.OperationalWasMax < OperationalBytes,
                         delegate () { return o.SetEventLog(OperationalLog, true, s.OperationalWasMax); });
                Step(r, "Remove the scheduled task that would have turned it off", true, delegate () { return o.CancelScheduledOff(); });
                Step(r, "Forget the notes on the event logs", s != null, delegate () { return o.SaveLoggingState(null); });
                Log.Info("admin: detailed SFTP logging off: " + why);
                RunSteps(r, true);
                return r;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        public string LoggingJson(bool turnOn, int minutes, string why)
        {
            bool busy;
            ReceiverReview r = turnOn ? LoggingOn(minutes, out busy) : LoggingOff(why, out busy);
            Json j = new Json(2048);
            if (busy) { j.Obj().P("ok", false).P("error", "Another change is still running.").EndObj(); return j.ToString(); }
            bool ok = r.Blocked.Count == 0;
            for (int i = 0; i < r.Steps.Count; i++) if (r.Steps[i].Status == "failed") ok = false;
            j.Obj().P("ok", ok);
            WriteSteps(j, r);
            j.EndObj();
            return j.ToString();
        }

        /// <summary>The switch's state for the page: on or off, until when, and the saved logs.</summary>
        void WriteLoggingJson(Json j, string config)
        {
            DateTime until;
            bool on = SftpLogConfig.IsOn(config ?? "", out until);
            LogState s = LogState.From(Ops.LoggingState());
            j.Key("logging").Obj().P("on", on).P("until", on && until > DateTime.MinValue ? TimeUtil.FromDate(until) : -1L)
             .P("started", s != null && s.Started > DateTime.MinValue ? TimeUtil.FromDate(s.Started) : -1L)
             .P("folder", Ops.LoggingFolder()).P("debugLog", Ops.GetEventLog(DebugLog).Exists);
            j.Key("files").Arr();
            List<ReceiverFile> files = Ops.LoggingFiles();
            for (int i = 0; i < files.Count; i++) j.Obj().P("name", files[i].Name).P("size", files[i].Size).P("when", files[i].When).EndObj();
            j.EndArr();
            j.EndObj();
        }
    }

    public sealed partial class WindowsReceiverOps
    {
        const string OffTask = "CubeScope SFTP logging off";

        public DateTime UtcNow() { return DateTime.UtcNow; }

        public long FreeBytes()
        {
            try { return new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).AvailableFreeSpace; }
            catch (Exception) { return -1; }
        }

        public EventLogInfo GetEventLog(string name)
        {
            EventLogInfo i = new EventLogInfo();
            try
            {
                using (EventLogConfiguration c = new EventLogConfiguration(name))
                {
                    i.Exists = true; i.Enabled = c.IsEnabled; i.MaxBytes = c.MaximumSizeInBytes;
                }
            }
            catch (EventLogNotFoundException) { }
            catch (Exception ex) { Log.Warn("admin: cannot read the " + name + " log's settings: " + ex.Message); }
            return i;
        }

        public string SetEventLog(string name, bool enabled, long maxBytes)
        {
            try
            {
                using (EventLogConfiguration c = new EventLogConfiguration(name))
                {
                    // a debug log takes a new size only while off, and is read only while off
                    if (c.IsEnabled && (!enabled || (maxBytes > 0 && c.MaximumSizeInBytes != maxBytes)))
                    {
                        c.IsEnabled = false;
                        c.SaveChanges();
                    }
                    if (maxBytes > 0 && c.MaximumSizeInBytes != maxBytes)
                    {
                        c.MaximumSizeInBytes = maxBytes;
                        c.SaveChanges();
                    }
                    if (enabled && !c.IsEnabled)
                    {
                        c.IsEnabled = true;
                        c.SaveChanges();
                    }
                }
                return null;
            }
            catch (Exception ex) { return "Windows refused to change the " + name + " log: " + ex.Message; }
        }

        public string ExportEvents(string[] names, DateTime fromUtc, DateTime toUtc, string fileName, out int count)
        {
            count = 0;
            string dir = EnsureLoggingFolder();
            List<KeyValuePair<DateTime, string>> lines = new List<KeyValuePair<DateTime, string>>();
            List<string> problems = new List<string>();
            string xpath = "*[System[TimeCreated[@SystemTime>='" + fromUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) +
                           "' and @SystemTime<='" + toUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "']]]";
            foreach (string name in names)
            {
                string shortName = name.Substring(name.IndexOf('/') + 1);
                try
                {
                    EventLogQuery q = new EventLogQuery(name, PathType.LogName, xpath);
                    using (EventLogReader rd = new EventLogReader(q))
                    {
                        rd.BatchSize = 512;
                        for (EventRecord e = rd.ReadEvent(); e != null; e = rd.ReadEvent())
                        {
                            using (e)
                            {
                                DateTime t = e.TimeCreated.HasValue ? e.TimeCreated.Value.ToUniversalTime() : fromUtc;
                                StringBuilder b = new StringBuilder();
                                // OpenSSH's events carry the message as their data; reading it there is far quicker than formatting
                                if (e.Properties != null)
                                    foreach (EventProperty p in e.Properties)
                                        if (p.Value != null) b.Append(b.Length > 0 ? " " : "").Append(Convert.ToString(p.Value, CultureInfo.InvariantCulture));
                                if (b.Length == 0) { try { b.Append(e.FormatDescription()); } catch (Exception) { } }
                                lines.Add(new KeyValuePair<DateTime, string>(t, t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC | " + shortName +
                                          " | pid " + (e.ProcessId.HasValue ? e.ProcessId.Value.ToString(CultureInfo.InvariantCulture) : "?") + " | " +
                                          b.ToString().Replace("\r", " ").Replace("\n", " ")));
                            }
                        }
                    }
                }
                catch (EventLogNotFoundException) { problems.Add(name + " is not on this server"); }
                catch (Exception ex) { problems.Add(name + ": " + ex.Message); }
            }
            // in time order; the two logs' lines interleave, each keeps its own order on a tie
            List<KeyValuePair<DateTime, string>> sorted = new List<KeyValuePair<DateTime, string>>(lines);
            int[] idx = new int[sorted.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, delegate (int a, int b) { int c = lines[a].Key.CompareTo(lines[b].Key); return c != 0 ? c : a.CompareTo(b); });
            string path = Path.Combine(dir, fileName);
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("CubeScope " + Program.Version + ": OpenSSH's detailed log on " + Environment.MachineName + ", " +
                            fromUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " to " + toUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                            " UTC (this server's clock is UTC" + TimeZoneInfo.Local.GetUtcOffset(toUtc).ToString(@"\+hh\:mm", CultureInfo.InvariantCulture).Replace("+-", "-") + ")");
                w.WriteLine("Lines: " + lines.Count.ToString(CultureInfo.InvariantCulture) + " from " + string.Join(" and ", names) +
                            (problems.Count > 0 ? ". Not read: " + string.Join("; ", problems.ToArray()) : ""));
                w.WriteLine();
                for (int i = 0; i < idx.Length; i++) w.WriteLine(lines[idx[i]].Value);
            }
            count = lines.Count;
            return problems.Count == names.Length ? "No OpenSSH log could be read: " + string.Join("; ", problems.ToArray()) : null;
        }

        public string ScheduleOff(DateTime atUtc, string exe)
        {
            string dir = EnsureLoggingFolder();
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Turns off CubeScope's detailed SFTP logging and saves its log. Set from CubeScope's Admin page.</Description></RegistrationInfo>\r\n" +
                "  <Triggers><TimeTrigger><StartBoundary>" + atUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT30M</ExecutionTimeLimit>" +
                "<Enabled>true</Enabled></Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--sftp-logging-off</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string file = Path.Combine(dir, "turn-off-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            string output;
            int rc;
            try { rc = Run(Sys("schtasks.exe"), "/create /tn \"" + OffTask + "\" /xml \"" + file + "\" /f", 30000, out output); }
            finally { try { File.Delete(file); } catch (Exception) { } }
            return rc == 0 ? null : "Windows did not take the scheduled task: " + output;
        }

        public string CancelScheduledOff()
        {
            string output;
            Run(Sys("schtasks.exe"), "/delete /tn \"" + OffTask + "\" /f", 30000, out output);
            return null;                                     // not there is fine
        }

        public string LoggingFolder() { return Path.Combine(HostOptions.DefaultDataDir(), "sftp-logs"); }

        /// <summary>The folder, made on first use for administrators and SYSTEM only: the lines name accounts and addresses.</summary>
        string EnsureLoggingFolder()
        {
            string dir = LoggingFolder();
            if (Directory.Exists(dir)) return dir;
            Directory.CreateDirectory(dir);
            try
            {
                DirectorySecurity ds = new DirectorySecurity();
                ds.SetAccessRuleProtection(true, false);
                InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(dir, ds);
            }
            catch (Exception ex) { Log.Warn("admin: could not limit " + dir + " to administrators: " + ex.Message); }
            return dir;
        }

        string StateFile() { return Path.Combine(LoggingFolder(), "logging-state.json"); }

        public string LoggingState()
        {
            try { return File.Exists(StateFile()) ? File.ReadAllText(StateFile(), Encoding.UTF8) : null; }
            catch (Exception) { return null; }
        }

        public string SaveLoggingState(string json)
        {
            if (json == null) { try { File.Delete(StateFile()); } catch (Exception) { } return null; }
            EnsureLoggingFolder();
            File.WriteAllText(StateFile(), json, new UTF8Encoding(false));
            return null;
        }

        public List<ReceiverFile> LoggingFiles()
        {
            List<ReceiverFile> list = new List<ReceiverFile>();
            try
            {
                if (!Directory.Exists(LoggingFolder())) return list;
                FileInfo[] f = new DirectoryInfo(LoggingFolder()).GetFiles("sftp-log-*.txt");
                Array.Sort(f, delegate (FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                for (int i = 0; i < f.Length && i < 20; i++)
                {
                    ReceiverFile r = new ReceiverFile();
                    r.Name = f[i].Name; r.Size = f[i].Length;
                    r.When = f[i].LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    list.Add(r);
                }
            }
            catch (Exception) { }
            return list;
        }

        public string LoggingFilePath(string name)
        {
            if (!ReceiverAdmin.SafeLogName(name)) return null;
            string p = Path.Combine(LoggingFolder(), name);
            return File.Exists(p) ? p : null;
        }
    }

    /// <summary>The pretend server's part: changes nothing on this computer.</summary>
    public sealed partial class SimulatedReceiverOps
    {
        public DateTime Now = new DateTime(2026, 11, 10, 15, 0, 0, DateTimeKind.Utc);
        public long Free = 50L << 30;
        public readonly Dictionary<string, EventLogInfo> Logs = new Dictionary<string, EventLogInfo>();
        public string State, ScheduledExe;
        public DateTime Scheduled = DateTime.MinValue;
        public DateTime ExportFrom, ExportTo;
        public readonly List<ReceiverFile> Saved = new List<ReceiverFile>();

        public DateTime UtcNow() { return Now; }
        public long FreeBytes() { return Free; }

        public EventLogInfo GetEventLog(string name)
        {
            EventLogInfo i;
            if (Logs.Count == 0)
            {
                // a fresh server: Operational on at 1 MB, Debug off
                EventLogInfo op = new EventLogInfo(); op.Exists = true; op.Enabled = true; op.MaxBytes = 1L << 20; Logs[ReceiverAdmin.OperationalLog] = op;
                EventLogInfo dbg = new EventLogInfo(); dbg.Exists = true; dbg.Enabled = false; dbg.MaxBytes = 1L << 20; Logs[ReceiverAdmin.DebugLog] = dbg;
            }
            if (!Logs.TryGetValue(name, out i)) i = new EventLogInfo();
            EventLogInfo copy = new EventLogInfo(); copy.Exists = i.Exists; copy.Enabled = i.Enabled; copy.MaxBytes = i.MaxBytes;
            return copy;
        }

        public string SetEventLog(string name, bool enabled, long maxBytes)
        {
            Calls.Add((enabled ? "log-on " : "log-off ") + name + (maxBytes > 0 ? " " + (maxBytes >> 20) + "MB" : ""));
            EventLogInfo i;
            if (!Logs.TryGetValue(name, out i)) return name + " is not on this server";
            i.Enabled = enabled;
            if (maxBytes > 0) i.MaxBytes = maxBytes;
            return null;
        }

        public string ExportEvents(string[] names, DateTime fromUtc, DateTime toUtc, string fileName, out int count)
        {
            Calls.Add("export");
            EventLogInfo d;
            if (Logs.TryGetValue(ReceiverAdmin.DebugLog, out d) && d.Enabled) { count = 0; return "The debug log is still on, so Windows cannot read it."; }
            ExportFrom = fromUtc; ExportTo = toUtc;
            count = 1234;
            ReceiverFile f = new ReceiverFile(); f.Name = fileName; f.Size = 98765; f.When = toUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Saved.Insert(0, f);
            return null;
        }

        public string ScheduleOff(DateTime atUtc, string exe) { Calls.Add("schedule"); Scheduled = atUtc; ScheduledExe = exe; return null; }
        public string CancelScheduledOff() { Calls.Add("unschedule"); Scheduled = DateTime.MinValue; return null; }
        public string LoggingFolder() { return @"C:\ProgramData\CubeScope\sftp-logs"; }
        public string LoggingState() { return State; }
        public string SaveLoggingState(string json) { Calls.Add(json == null ? "forget" : "note"); State = json; return null; }
        public List<ReceiverFile> LoggingFiles() { return new List<ReceiverFile>(Saved); }

        public string LoggingFilePath(string name)
        {
            if (!ReceiverAdmin.SafeLogName(name)) return null;
            foreach (ReceiverFile f in Saved) if (f.Name == name) return Path.Combine(LoggingFolder(), name);
            return null;
        }
    }
}
