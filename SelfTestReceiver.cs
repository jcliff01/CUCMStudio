using System;
using System.Collections.Generic;

namespace CubeScope
{
    /// <summary>The Admin page's checks. They run against the stand-in, so they change nothing on this computer.</summary>
    public static partial class SelfTest
    {
        static void TestReceiver()
        {
            bool echo = Log.Echo;
            Log.Echo = false;
            try
            {
                TestSshdConfig();
                TestReceiverSettings();
                TestReceiverSetup();
                TestReceiverParsing();
                TestReceiverGuards();
                TestSftpLoggingText();
                TestSftpLoggingSwitch();
            }
            finally { Log.Echo = echo; }
        }

        static void TestSftpLoggingText()
        {
            Group("admin: detailed SFTP logging in OpenSSH's settings");
            string a = SshdConfig.Update(SimulatedReceiverOps.SampleConfig, 2121, "cube-cdr", @"C:\CubeCDR");
            DateTime until = new DateTime(2026, 11, 10, 15, 30, 0, DateTimeKind.Utc);
            string on = SftpLogConfig.On(a, "cube-cdr", until);
            Check(on != null, "turned on");
            if (on == null) return;
            Check(on.StartsWith(SftpLogConfig.Marker + " until 2026-11-10T15:30:00Z", StringComparison.Ordinal), "a marked block at the very top, so it wins over any line below");
            Check(on.IndexOf("\nSyslogFacility AUTH\nLogLevel DEBUG2\n", StringComparison.Ordinal) > 0, "OpenSSH logs each connection step into Windows' event log");
            Eq(Occurs(on, "ForceCommand internal-sftp -l DEBUG3"), 1, "and the account's file transfers request by request");
            DateTime u;
            Check(SftpLogConfig.IsOn(on, out u) && u == until, "the end is read back");
            Check(!SftpLogConfig.IsOn(a), "and is not there before");
            Eq(SftpLogConfig.Off(on), a, "turning it off puts the file back exactly");
            string again = SftpLogConfig.On(on, "cube-cdr", until.AddMinutes(30));
            Eq(Occurs(again, SftpLogConfig.Marker), 1, "turning it on again keeps one block");
            Check(SftpLogConfig.IsOn(again, out u) && u == until.AddMinutes(30), "and moves the end");
            Eq(Occurs(again, "-l DEBUG3"), 1, "with the file-transfer flag once");
            Eq(SftpLogConfig.Off(again), a, "and still comes off exactly");
            string crlf = a.Replace("\n", "\r\n");
            Eq(SftpLogConfig.Off(SftpLogConfig.On(crlf, "cube-cdr", until)), crlf, "Windows line endings kept both ways");
            Eq(SshdConfig.Update(on, 2121, "cube-cdr", @"C:\CubeCDR"), on, "an Apply while it is on keeps it on");
            Check(SshdConfig.Update(a, 2121, "cube-cdr", @"C:\CubeCDR").IndexOf("-l DEBUG3", StringComparison.Ordinal) < 0, "and while it is off adds nothing");
            string own = "LogLevel VERBOSE\n" + a;
            Eq(SftpLogConfig.Off(SftpLogConfig.On(own, "cube-cdr", until)), own, "someone's own LogLevel line stays and comes back into force");
            Check(SftpLogConfig.On(SimulatedReceiverOps.SampleConfig, "cube-cdr", until) == null, "no account block: nothing to turn on");
            Eq(SftpLogConfig.Off(a), a, "turning off what is off changes nothing");
            Check(SftpLogConfig.HasDetailedLine(on) && !SftpLogConfig.HasDetailedLine(a), "a left-over file-transfer flag is found");

            Check(ReceiverAdmin.SafeLogName("sftp-log-20261110-1500-to-1530.txt"), "a saved log's name");
            Check(!ReceiverAdmin.SafeLogName(@"..\sshd_config"), "a path out of the folder is refused");
            Check(!ReceiverAdmin.SafeLogName("sftp-log-../x.txt"), "dots are refused");
            Check(!ReceiverAdmin.SafeLogName("sftp-log-a b.txt") && !ReceiverAdmin.SafeLogName(null), "spaces and nothing are refused");
        }

        static void TestSftpLoggingSwitch()
        {
            Group("admin: the detailed-logging switch");
            SimulatedReceiverOps o = new SimulatedReceiverOps();
            ReceiverAdmin a = new ReceiverAdmin(o, true, "t");
            bool busy;
            ReceiverReview first = a.LoggingOn(30, out busy);
            Eq(first.Blocked.Count, 1, "not set up yet: refused");
            Eq(o.Calls.Count, 0, "and nothing touched");
            a.Apply(GoodSettings("S3cret-Pass!"), out busy);
            string before = o.Config;
            DateTime started = o.Now;
            Eq(a.LoggingOn(3, out busy).Blocked.Count, 1, "under 5 minutes is refused");
            o.Calls.Clear();
            ReceiverReview on = a.LoggingOn(30, out busy);
            Eq(on.Blocked.Count, 0, "turned on");
            Eq(string.Join(",", o.Calls), "write,test,note,log-on OpenSSH/Debug 256MB,log-on OpenSSH/Operational 64MB,schedule,restart",
               "settings first (they can be refused), the notes before the logs change, then the task and the restart");
            Eq(o.Scheduled, started.AddMinutes(30), "the task turns it off 30 minutes on");
            Check(o.ScheduledExe == o.ExePath(), "running this exe");
            Check(SftpLogConfig.IsOn(o.Config), "OpenSSH's settings have it");
            Check(a.StateJson().IndexOf("\"logging\":{\"on\":true", StringComparison.Ordinal) > 0, "the page shows it on");

            o.Calls.Clear(); o.Now = o.Now.AddMinutes(10);
            a.LoggingOn(60, out busy);
            Eq(string.Join(",", o.Calls), "write,test,schedule", "on again: only the end moves, no restart");
            Eq(o.Scheduled, o.Now.AddMinutes(60), "to 60 minutes from now");
            o.Calls.Clear();
            a.Apply(GoodSettings(""), out busy);
            Eq(o.Calls.Count, 0, "an Apply while it is on changes nothing");

            o.Calls.Clear(); o.Now = o.Now.AddMinutes(5);
            ReceiverReview off = a.LoggingOff("test", out busy);
            Eq(o.Config, before, "OpenSSH's settings are back exactly");
            Eq(string.Join(",", o.Calls), "write,test,restart,log-off OpenSSH/Debug,export,log-off OpenSSH/Debug 1MB,log-on OpenSSH/Operational 1MB,unschedule,forget",
               "settings and restart, the debug log off before reading it, the lines saved, the logs put back, the task and notes gone");
            Eq(o.ExportFrom, started.AddMinutes(-1), "the lines from a minute before it was turned on");
            Eq(o.ExportTo, o.Now, "to now");
            Check(o.Saved.Count == 1 && ReceiverAdmin.SafeLogName(o.Saved[0].Name), "one saved log, with a name the page can ask for: " + (o.Saved.Count > 0 ? o.Saved[0].Name : ""));
            EventLogInfo dbg = o.GetEventLog(ReceiverAdmin.DebugLog), op = o.GetEventLog(ReceiverAdmin.OperationalLog);
            Check(!dbg.Enabled && dbg.MaxBytes == 1L << 20 && op.MaxBytes == 1L << 20, "both event logs as they were");
            Check(o.State == null && o.Scheduled == DateTime.MinValue, "no notes, no task left");
            string state = a.StateJson();
            Check(state.IndexOf("\"logging\":{\"on\":false", StringComparison.Ordinal) > 0 && state.IndexOf(o.Saved[0].Name, StringComparison.Ordinal) > 0, "the page shows it off, with the saved log");
            bool okAll = true;
            foreach (ReceiverStep st in off.Steps) if (st.Status == "failed") okAll = false;
            Check(okAll, "every step done");

            o.Calls.Clear();
            ReceiverReview again = a.LoggingOff("again", out busy);
            Eq(string.Join(",", o.Calls), "unschedule", "off when off: only makes sure no task is left");
            Check(again.Notes.Count == 1, "and says it was off");

            Group("admin: the detailed-logging switch refuses or stops when it should");
            SimulatedReceiverOps full = new SimulatedReceiverOps();
            new ReceiverAdmin(full, true, "t").Apply(GoodSettings("S3cret-Pass!"), out busy);
            full.Free = 300L << 20; full.Calls.Clear();
            ReceiverReview low = new ReceiverAdmin(full, true, "t").LoggingOn(30, out busy);
            Check(low.Blocked.Count == 1 && low.Blocked[0].IndexOf("300 MB", StringComparison.Ordinal) > 0, "under 1 GB free on the event logs' drive: refused, saying how much");
            Eq(full.Calls.Count, 0, "nothing touched");

            SimulatedReceiverOps bad = new SimulatedReceiverOps();
            new ReceiverAdmin(bad, true, "t").Apply(GoodSettings("S3cret-Pass!"), out busy);
            string good = bad.Config;
            bad.FailTest = "line 1: Bad configuration option: LogLevel"; bad.Calls.Clear();
            ReceiverReview rej = new ReceiverAdmin(bad, true, "t").LoggingOn(30, out busy);
            Eq(bad.Config, good, "OpenSSH refuses the settings: they are put back");
            Eq(string.Join(",", bad.Calls), "write,test,restore", "and nothing else is done");
            Check(rej.Steps[0].Status == "failed" && rej.Steps[1].Status == "not-run", "the later steps say they did not run");

            SimulatedReceiverOps had = new SimulatedReceiverOps();
            ReceiverAdmin ha = new ReceiverAdmin(had, true, "t");
            ha.Apply(GoodSettings("S3cret-Pass!"), out busy);
            had.GetEventLog(ReceiverAdmin.DebugLog);
            had.Logs[ReceiverAdmin.DebugLog].Enabled = true; had.Logs[ReceiverAdmin.DebugLog].MaxBytes = 10L << 20;
            ha.LoggingOn(15, out busy);
            had.Calls.Clear();
            ha.LoggingOff("test", out busy);
            Check(had.Calls.Contains("log-off OpenSSH/Debug") && had.Calls.Contains("export") && had.Calls.Contains("log-on OpenSSH/Debug 10MB"),
                  "a debug log someone had on is turned off to read it, then back on at its own size: " + string.Join(",", had.Calls));

            SimulatedReceiverOps none = new SimulatedReceiverOps();
            ReceiverAdmin na = new ReceiverAdmin(none, true, "t");
            na.Apply(GoodSettings("S3cret-Pass!"), out busy);
            EventLogInfo opOnly = new EventLogInfo(); opOnly.Exists = true; opOnly.Enabled = true; opOnly.MaxBytes = 128L << 20;
            none.Logs.Clear(); none.Logs[ReceiverAdmin.OperationalLog] = opOnly;
            none.Calls.Clear();
            ReceiverReview nd = na.LoggingOn(15, out busy);
            Check(nd.Notes.Count == 1 && nd.Notes[0].IndexOf("no OpenSSH/Debug", StringComparison.Ordinal) > 0, "no debug log on this server: said, and carried on");
            Eq(string.Join(",", none.Calls), "write,test,note,schedule,restart", "an Operational log already big enough is left alone");
            Check(na.LoggingJson(false, 0, "test").IndexOf("\"ok\":true", StringComparison.Ordinal) > 0, "and off again cleanly");
        }

        static int Occurs(string text, string what)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }

        static void TestSshdConfig()
        {
            Group("admin: OpenSSH settings file");
            string a = SimulatedReceiverOps.SampleConfig;
            Eq(SshdConfig.ActivePort(a), 0, "a commented port means the default");
            string b = SshdConfig.Update(a, 2121, "cube-cdr", @"C:\CubeCDR");
            Eq(SshdConfig.ActivePort(b), 2121, "port set");
            Eq(Occurs(b, "Port 2121"), 1, "one port line");
            Check(b.IndexOf("#Port 22", StringComparison.Ordinal) < 0, "the commented line became the setting");
            Check(b.IndexOf("Match Group administrators", StringComparison.Ordinal) < b.IndexOf("Match User cube-cdr", StringComparison.Ordinal), "the block goes after the existing Match");
            Check(b.EndsWith("X11Forwarding no\n", StringComparison.Ordinal), "ends with one line ending");
            Eq(SshdConfig.Update(b, 2121, "cube-cdr", @"C:\CubeCDR"), b, "doing it twice changes nothing");

            string acct, folder;
            Check(SshdConfig.FindBlock(b, out acct, out folder), "block found");
            Eq(acct, "cube-cdr", "account read back");
            Eq(folder, @"C:\CubeCDR", "folder read back");

            string c = SshdConfig.Update(b, 2222, "cube-cdr", @"D:\Call Records");
            Eq(SshdConfig.ActivePort(c), 2222, "port changed");
            Eq(Occurs(c, "Match User cube-cdr"), 1, "the block is replaced, not repeated");
            Eq(Occurs(c, SshdConfig.Marker), 1, "one marker");
            Check(c.IndexOf("ChrootDirectory \"D:\\Call Records\"", StringComparison.Ordinal) > 0, "a folder with a space is quoted");
            Check(SshdConfig.FindBlock(c, out acct, out folder) && folder == @"D:\Call Records", "quoted folder read back");

            string crlf = a.Replace("\n", "\r\n");
            string d = SshdConfig.Update(crlf, 2121, "cube-cdr", @"C:\CubeCDR");
            Eq(Occurs(d, "\n"), Occurs(d, "\r\n"), "Windows line endings kept");

            string e = SshdConfig.Update("Port 22\nPort 2200\n\nMatch Group x\n    Port 99\n", 2121, "u", @"C:\X");
            Eq(Occurs(e, "Port 2121"), 1, "several ports become one");
            Check(e.IndexOf("Port 2200", StringComparison.Ordinal) < 0, "the other port is gone");
            Check(e.IndexOf("    Port 99", StringComparison.Ordinal) > 0, "a line inside a Match is left alone");

            string f = SshdConfig.Update("Subsystem sftp sftp-server.exe\n", 2121, "u", @"C:\X");
            Check(f.StartsWith("Port 2121\n", StringComparison.Ordinal), "added at the top when the file has no port line");

            string hand = "#Port 22\nMatch User cube-cdr\n    ChrootDirectory C:\\Old\nMatch Group admins\n    X11Forwarding no\n";
            string g = SshdConfig.Update(hand, 2121, "cube-cdr", @"C:\CubeCDR");
            Check(g.IndexOf(@"C:\Old", StringComparison.Ordinal) < 0, "a hand-made block for the account is replaced");
            Check(g.IndexOf("Match Group admins", StringComparison.Ordinal) > 0, "the block after it is kept");

            Eq(SshdConfig.Warnings(a).Count, 0, "nothing to warn about");
            Eq(SshdConfig.Warnings("AllowUsers someone\n").Count, 1, "AllowUsers is pointed out");
        }

        static ReceiverSettings ParseSettings(string port, string folder, string account, string password, string allowed, out string error)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            if (port != null) o["port"] = double.Parse(port, System.Globalization.CultureInfo.InvariantCulture);
            o["folder"] = folder; o["account"] = account; o["password"] = password; o["allowed"] = allowed;
            return ReceiverSettings.FromJson(o, out error);
        }

        static void Refused(string port, string folder, string account, string password, string allowed, string what)
        {
            string error;
            ReceiverSettings s = ParseSettings(port, folder, account, password, allowed, out error);
            Check(s == null && !string.IsNullOrEmpty(error), what);
        }

        static void TestReceiverSettings()
        {
            Group("admin: settings are checked");
            string error;
            ReceiverSettings s = ParseSettings("2121", @"C:\CubeCDR\", " Cube-CDR ", "abc123!", "192.0.2.10, 198.51.100.0/24 192.0.2.10/32", out error);
            Check(s != null, "good settings: " + error);
            if (s == null) return;
            Eq(s.Port, 2121, "port");
            Eq(s.Folder, @"C:\CubeCDR", "folder without the trailing slash");
            Eq(s.Account, "cube-cdr", "account in lower case");
            Eq(string.Join("|", s.Allowed), "192.0.2.10|198.51.100.0/24", "addresses cleaned and without repeats");

            const string F = @"C:\CubeCDR", A = "192.0.2.10";
            Refused("0", F, "u", "", A, "port 0");
            Refused("70000", F, "u", "", A, "port too high");
            Refused("21.5", F, "u", "", A, "half a port");
            Refused(null, F, "u", "", A, "no port");
            Refused("2121", F, "Administrator", "", A, "a Windows built-in account");
            Refused("2121", F, "a b", "", A, "a space in the account");
            Refused("2121", F, "u\" x", "", A, "a quote in the account");
            Refused("2121", F, "u", "has space", A, "a space in the password");
            Refused("2121", F, "u", "what?", A, "a question mark in the password");
            Refused("2121", @"C:\", "u", "", A, "the top of a drive");
            Refused("2121", @"relative\path", "u", "", A, "a relative folder");
            Refused("2121", @"\\server\share", "u", "", A, "a network share");
            Refused("2121", @"C:\Cube""CDR", "u", "", A, "a quote in the folder");
            Refused("2121", @"C:\a\..\Windows", "u", "", A, "climbing out of a folder");
            Refused("2121", Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\Temp\x", "u", "", A, "a folder inside Windows");
            Refused("2121", F, "u", "", "", "no CUBE address");
            Refused("2121", F, "u", "", "any", "the word any");
            Refused("2121", F, "u", "", "0.0.0.0/1", "everywhere");
            Refused("2121", F, "u", "", "192.0.2.300", "an impossible address");
            Refused("2121", F, "u", "", "192.0.2.1/0", "a mask of nothing");
            Refused("2121", F, "u", "", "192.0.2.1 & calc", "a command tacked on");
        }

        static ReceiverSettings GoodSettings(string password)
        {
            ReceiverSettings s = new ReceiverSettings();
            s.Password = password;
            s.Allowed.Add("192.0.2.10");
            return s;
        }

        static string Statuses(ReceiverReview r)
        {
            List<string> a = new List<string>();
            for (int i = 0; i < r.Steps.Count; i++) a.Add(r.Steps[i].Status ?? "");
            return string.Join(",", a);
        }

        static void TestReceiverSetup()
        {
            Group("admin: setting up a new server");
            const string Secret = "S3cret-Pass!";
            SimulatedReceiverOps o = new SimulatedReceiverOps();
            ReceiverAdmin a = new ReceiverAdmin(o, true, "t");
            ReceiverReview plan = a.Review(GoodSettings(Secret));
            Eq(plan.Blocked.Count, 0, "nothing in the way");
            Eq(o.Calls.Count, 0, "reviewing changes nothing");
            bool busy;
            ReceiverReview r = a.Apply(GoodSettings(Secret), out busy);
            Check(!busy, "not busy");
            Eq(string.Join(",", o.Calls), "install,start,create,folder,write,test,restart,firewall", "in order");
            Eq(Statuses(r), "done,done,done,done,done,done,done,done", "every step done");
            Eq(SshdConfig.ActivePort(o.Config), 2121, "port set");
            Check(a.StateJson().IndexOf(Secret, StringComparison.Ordinal) < 0, "the password is not sent back");

            Group("admin: running it again");
            o.Calls.Clear();
            ReceiverReview again = a.Review(GoodSettings(""));
            for (int i = 0; i < again.Steps.Count - 1; i++) Check(!again.Steps[i].Needed, "already done: " + again.Steps[i].Text);
            Check(again.Steps[again.Steps.Count - 1].Needed, "the final check always runs");
            a.Apply(GoodSettings(""), out busy);
            Eq(o.Calls.Count, 0, "nothing changed the second time");
            string json = a.ApplyJson(GoodSettings(Secret));
            Check(json.IndexOf(Secret, StringComparison.Ordinal) < 0, "the password is not in the result");
            Eq(string.Join(",", o.Calls), "password", "a new password only changes the password");

            Group("admin: the folder and account were made by hand");
            SimulatedReceiverOps h = new SimulatedReceiverOps();
            h.Exe = @"C:\x\sshd.exe"; h.Service = "Running"; h.Config = SimulatedReceiverOps.SampleConfig; h.Kind = 1; h.Folder = true;
            new ReceiverAdmin(h, true, "t").Apply(GoodSettings(""), out busy);
            Eq(string.Join(",", h.Calls), "folder,write,test,restart,firewall", "only what is missing");

            Group("admin: OpenSSH rejects the new settings");
            SimulatedReceiverOps bad = new SimulatedReceiverOps();
            bad.FailTest = "line 9: Bad configuration option";
            string before = null;
            ReceiverReview f = new ReceiverAdmin(bad, true, "t").Apply(GoodSettings(Secret), out busy);
            before = SimulatedReceiverOps.SampleConfig;
            Eq(bad.Config, before, "the old settings are put back");
            Eq(string.Join(",", bad.Calls), "install,start,create,folder,write,test,restore", "stops there");
            Eq(Statuses(f), "done,done,done,done,failed,not-run,not-run,not-run", "later steps did not run");
            Check(f.Steps[4].Detail.IndexOf("Bad configuration", StringComparison.Ordinal) > 0, "says why");

            Group("admin: refused before anything changes");
            SimulatedReceiverOps ftp = new SimulatedReceiverOps();
            ftp.Exe = @"C:\x\sshd.exe"; ftp.Service = "Running"; ftp.Config = SimulatedReceiverOps.SampleConfig; ftp.Kind = 1;
            ftp.OtherPort = 2121; ftp.OtherBanner = "220-FileZilla Server";
            ReceiverReview b1 = new ReceiverAdmin(ftp, true, "t").Apply(GoodSettings(""), out busy);
            Eq(b1.Blocked.Count, 1, "the port is in use");
            Check(b1.Blocked[0].IndexOf("FileZilla", StringComparison.Ordinal) > 0, "says what is using it");
            Eq(ftp.Calls.Count, 0, "nothing done");

            SimulatedReceiverOps adm = new SimulatedReceiverOps();
            adm.Kind = 2;
            Eq(new ReceiverAdmin(adm, true, "t").Apply(GoodSettings(Secret), out busy).Blocked.Count, 1, "an administrator account is refused");
            Eq(adm.Calls.Count, 0, "its password is not touched");

            SimulatedReceiverOps none = new SimulatedReceiverOps();
            Eq(new ReceiverAdmin(none, true, "t").Apply(GoodSettings(""), out busy).Blocked.Count, 1, "a new account needs a password");
            Eq(none.Calls.Count, 0, "nothing done without it");

            SimulatedReceiverOps nomsi = new SimulatedReceiverOps();
            nomsi.Msi = null;
            Eq(new ReceiverAdmin(nomsi, true, "t").Review(GoodSettings(Secret)).Blocked.Count, 1, "no installer and nothing installed");
        }

        static void TestReceiverParsing()
        {
            Group("admin: reading Windows' answers");
            string events =
                "Event[0]:\r\n  Log Name: OpenSSH/Operational\r\n  Date: 2026-11-09T13:16:00.000\r\n  Description: \r\nsshd: Accepted password for cube-cdr from 192.0.2.10 port 52001 ssh2\r\n\r\n" +
                "Event[1]:\r\n  Date: 2026-11-09T13:15:00.000\r\n  Description: \r\nsshd: Connection closed by 127.0.0.1 port 5000\r\n\r\n" +
                "Event[2]:\r\n  Date: 2026-11-09T13:14:00.000\r\n  Description: \r\nsshd: Failed password for cube-cdr from 192.0.2.10 port 52000 ssh2\r\n";
            List<string> s = ReceiverAdmin.ParseSignIns(events, 8);
            Eq(s.Count, 2, "sign-ins only");
            Eq(s[0], "2026-11-09 13:16:00  Accepted password for cube-cdr from 192.0.2.10 port 52001 ssh2", "time and text");
            Check(s[1].IndexOf("Failed password", StringComparison.Ordinal) > 0, "failures shown");
            Eq(ReceiverAdmin.ParseSignIns(events, 1).Count, 1, "limited");
            Eq(ReceiverAdmin.ParseSignIns(null, 8).Count, 0, "nothing");

            int port;
            string rule = "\r\nRule Name:   CUBE CDR SFTP\r\n----\r\nEnabled:   Yes\r\nRemoteIP:   192.0.2.10/32,198.51.100.0/24\r\nProtocol:   TCP\r\nLocalPort:   2121\r\nRemotePort:   Any\r\n";
            Eq(ReceiverAdmin.ParseFirewall(rule, out port), "192.0.2.10,198.51.100.0/24", "allowed addresses");
            Eq(port, 2121, "port");
            Eq(ReceiverAdmin.ParseFirewall("Regelname: x", out port), "", "labels it cannot read");
        }

        static void TestReceiverGuards()
        {
            Group("admin: who may use it");
            Check(WebServer.AdminHostAllowed("localhost"), "localhost");
            Check(WebServer.AdminHostAllowed("127.0.0.1"), "127.0.0.1");
            Check(!WebServer.AdminHostAllowed("example.com"), "another name, even if it points here");
            Check(!WebServer.AdminHostAllowed("192.0.2.5"), "the server's own network address");
            Check(!WebServer.AdminTokenOk(null, null), "no window");
            Check(!WebServer.AdminTokenOk(new ReceiverAdmin(new SimulatedReceiverOps(), true, "abc"), null), "no key");
            Check(!WebServer.AdminTokenOk(new ReceiverAdmin(new SimulatedReceiverOps(), true, "abc"), "abd"), "wrong key");
            Check(WebServer.AdminTokenOk(new ReceiverAdmin(new SimulatedReceiverOps(), true, "abc"), "abc"), "this window's key");
            string t1 = ReceiverAdmin.NewToken(), t2 = ReceiverAdmin.NewToken();
            Eq(t1.Length, 32, "a long key");
            Check(t1 != t2, "a new key each window");
        }
    }
}
