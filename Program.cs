using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace CubeScope
{
    public static class Program
    {
        public const string Version = "1.2";

        static void Usage()
        {
            Console.WriteLine(@"
CubeScope " + Version + @" - Cisco CUBE call accounting (syslog) dashboard and reports.

Reads the %VOIPAAA-5-VOIP_CALL_HISTORY / VOIP_FEAT_HISTORY records a CUBE sends to a syslog
server, joins the legs of every call (the CUBE accounts each direction separately), and shows
results, release causes, who hung up, timings, packet counts, transfers and failure traces.

USAGE
  CubeScope.exe --logs <folder> [options]
  Double-click CubeScope.exe to choose the folder in a window instead.
  CubeScope.exe --admin  sets this server up to receive CUBE call record files over SFTP, from a page.
                         Or right-click the exe, Run as administrator, and cancel the folder window.

WHAT TO READ
  --logs <folder>        the CUBE's hourly files (<name>-yyyy-mm-dd-hh.log) or call record files
                         (cdr.<hostname>.<time>, from gw-accounting file), or a folder holding
                         one folder per CUBE; looked for two folder levels down
  --cube <name>          only this CUBE (the name its files start with); repeat for several
  --days <n>             the newest n days (default 14)      --all   every file
  --from <t> --to <t>    a range on the CUBE's clock: yyyy-MM-dd or yyyy-MM-ddTHH:mm
  --pattern <glob>       file name pattern (default *.log)
  --rules <file>         number rules (default: cubescope.rules beside the exe, else built in)
  --threads <n>          files parsed at once (default: processor count, at most 8)
  --server-zone <z>      the syslog server's zone, for the clock check and time-zone views:
                         UTC, UTC-6, UTC+5:30, local, or a Windows zone name such as
                         ""Mountain Standard Time"" (default: worked out from the CUBEs' labels)
  --zone <z>             the clock times are shown on (and --from/--to are read on): cube (each
                         CUBE's own, the default), server (the syslog server's), or a zone as for
                         --server-zone; CUBEs in different zones line up on any but cube

WHAT TO DO (with none of these, --summary)
  --serve [port]         dashboard on http://localhost:<port>/ (default 8792), following new records
  --open                 open the browser on the dashboard
  --no-live              do not follow the files after the first read
  --summary              per-day tables (and per-hour for one to three days) on the console
  --bucket <b>           1m 5m 15m 30m 1h 3h 6h 1d - the summary's second table and --report/--json
  --filter ""<f>""         only calls matching the filter (see FILTERS)
  --trace <guid|number>  every leg, feature, internal error and failure trace of the matching
                         calls, with the raw log lines they came from (the newest 20)
  --csv <file>           one row per call
  --report <file>        the dashboard for this range and filter as one HTML file
  --json                 the dashboard's state as JSON on standard output
  --rules-sample         print the built-in rules file (save it as cubescope.rules to edit)
  --dashboard <file>     serve this dashboard.html instead of the built-in one (development)
  --selftest             run the built-in tests       --pickcheck   check the folder window plumbing

SERVICE AND ALARMS
  --install-service      (administrator) run CubeScope as the Windows service ""CubeScope"": it starts with
                         Windows, keeps each CUBE's newest 24 hours (--hours), answers this computer only,
                         and runs the alarms set in the page's Alarms window. Give --logs, and --serve
                         <port> for a port other than 8792. Run it again to change them.
  --uninstall-service    (administrator) stop and remove the service; its settings and log stay
  --hours <n>            keep only each CUBE's newest n hours of files, read afresh every n/4 hours
  --local-only           answer this computer only
  --https <port>         also answer other computers over HTTPS on this port, after a Windows sign-in
                         (needs the certificate bound to the port: the Admin page's Remote access does it)
  --allow-group <name>   with --https: the local group whose members may sign in (default ""CubeScope Users"";
                         this server's administrators always may)
  --alarms               run the alarms here (settings and log in --data-dir, default
                         %ProgramData%\CubeScope)

FILTERS   clauses separated by spaces must all hold; commas are alternatives; ! negates a clause
  called= calling= connected= dialed= peer= any=   251 = starts with 251, 251* and ? wildcards,
                         ""2515551234"" exactly, re:<regex>; a bare number means any=<number>
  result=answered|abandoned|noanswer|busy|rejected|failed|unanswered
  legfail=251 (a leg sent to this number that nobody answered, even on a call that reads as answered)
  cause=17,21 (the deciding cause)  legcause=47 (any leg)  released=caller|called|network|tie
  media=none|oneway|lowrate|twoway, bad (none or one-way), problem (any of the three); audio= is the same
  talk>=30  ring<5  dur>600  a2t>3  legs>=3  transfers>=1  codec=210  tag=dialer
  has=dump|iec|transfer|cfa|stub|unbound  src=<cube name>

EXAMPLES
  CubeScope.exe --logs D:\logs\10.1.1.5 --summary
  CubeScope.exe --logs D:\logs --serve --open
  CubeScope.exe --logs D:\logs\10.1.1.5 --from 2026-09-28 --to 2026-09-29 --bucket 15m --summary
  CubeScope.exe --logs D:\logs\10.1.1.5 --filter ""called=251* result=unanswered"" --csv failed.csv
  CubeScope.exe --logs D:\logs\10.1.1.5 --trace 9A9A9A9AB1B111F1C2C2D3D3D3D3D3D3
  CubeScope.exe --logs ""D:\Syslog Server\Logs"" --install-service
");
        }

        public static int Main(string[] args)
        {
            // the installed service: Windows starts it with --service first and its settings after
            if (args.Length > 0 && args[0] == "--service")
            {
                string[] rest = new string[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                if (Environment.UserInteractive)
                {
                    Console.Error.WriteLine("--service is how Windows starts the installed service. From a prompt use --install-service,");
                    Console.Error.WriteLine("or --serve to run CubeScope here.");
                    return 1;
                }
                Log.Echo = false;
                string err0;
                HostOptions h0 = ParseHost(rest, out err0);
                Log.Init(Path.Combine(h0 != null ? h0.DataDir : HostOptions.DefaultDataDir(), "logs"));
                System.ServiceProcess.ServiceBase.Run(new CubeScopeService(rest));
                return 0;
            }
            bool doubleClick = args.Length == 0 && Launcher.LaunchedByDoubleClick();
            try
            {
                if (doubleClick)
                {
                    string[] picked = Launcher.AskForArgs();
                    if (picked == null && CubeScopeService.IsAdmin())
                    {
                        Console.WriteLine("No folder chosen. Opening the Admin page.");
                        picked = new string[] { "--admin" };
                    }
                    if (picked == null)
                    {
                        Console.WriteLine("No folder chosen.");
                        Console.WriteLine("To set up the SFTP receiver, right-click CubeScope.exe, choose Run as administrator, and cancel the folder window.");
                        PauseBeforeClose();
                        return 1;
                    }
                    args = picked;
                }
                int rc = Run(args);
                if (doubleClick && rc != 0) PauseBeforeClose();
                return rc;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                if (doubleClick) PauseBeforeClose();
                return 2;
            }
        }

        static void PauseBeforeClose()
        {
            Console.WriteLine();
            Console.Write("Press Enter to close this window.");
            try { Console.ReadLine(); } catch (Exception) { }
        }

        static string Arg(string[] a, ref int i)
        {
            if (i + 1 >= a.Length) throw new Exception("missing value after " + a[i]);
            return a[++i];
        }

        /// <summary>
        /// A path value. Nothing on this command line is positional, so words after the path that are
        /// not options are the rest of a path with spaces typed without quotes (D:\Syslog Server\Logs).
        /// </summary>
        public static string PathArg(string[] a, ref int i)
        {
            string opt = a[i];
            string v = Arg(a, ref i);
            int first = i;
            while (i + 1 < a.Length && !a[i + 1].StartsWith("-", StringComparison.Ordinal)) v += " " + a[++i];
            if (i > first)
                Console.Error.WriteLine("note: read " + opt + " as \"" + v + "\" - put a path with spaces in quotes");
            return v;
        }

        static Rules LoadRules(string rulesPath)
        {
            Rules rules;
            if (rulesPath != null) rules = Rules.Load(rulesPath);
            else
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cubescope.rules");
                rules = File.Exists(beside) ? Rules.Load(beside) : Rules.Default();
            }
            for (int i = 0; i < rules.Errors.Count; i++) Log.Warn("rules: " + rules.Errors[i]);
            return rules;
        }

        /// <summary>The service's settings, from the command line --install-service wrote for it.</summary>
        public static HostOptions ParseHost(string[] args, out string err)
        {
            err = null;
            HostOptions h = new HostOptions();
            string rulesPath = null;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--logs": h.Load.Root = PathArg(args, ref i); break;
                        case "--cube": h.Load.Cubes.Add(Arg(args, ref i)); break;
                        case "--pattern": h.Load.Pattern = Arg(args, ref i); break;
                        case "--threads": h.Load.Threads = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--hours": h.Load.Hours = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--serve":
                            if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                                h.Port = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture);
                            break;
                        case "--local-only": h.LocalOnly = true; break;
                        case "--alarms": h.Alarms = true; break;
                        case "--no-live": h.Live = false; break;
                        case "--data-dir": h.DataDir = PathArg(args, ref i); break;
                        case "--rules": rulesPath = PathArg(args, ref i); break;
                        case "--server-zone": h.ServerZone = Arg(args, ref i); break;
                        case "--dashboard": h.Dashboard = PathArg(args, ref i); break;
                        case "--https": h.HttpsPort = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                        case "--allow-group": h.AllowGroup = Arg(args, ref i); break;
                        default: err = "unknown option " + args[i]; return null;
                    }
                }
            }
            catch (Exception ex) { err = ex.Message; return null; }
            if (string.IsNullOrEmpty(h.Load.Root)) { err = "--logs <folder> is required"; return null; }
            if (h.Load.Hours <= 0) h.Load.Hours = 24;
            h.Rules = LoadRules(rulesPath);
            return h;
        }

        /// <summary>
        /// The Admin page alone, with no logs to read. --admin asks Windows for administrator rights if it has
        /// none. --admin-simulate shows the same page on a pretend server and changes nothing (for testing).
        /// </summary>
        static int RunAdmin(string[] args)
        {
            bool simulate = args[0] == "--admin-simulate", open = true;
            int port = 0;
            string dashboard = null;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--serve": port = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--dashboard": dashboard = PathArg(args, ref i); break;
                    case "--no-open": open = false; break;
                    default: Console.Error.WriteLine("unknown option " + args[i]); return 1;
                }
            }
            if (!simulate && !CubeScopeService.IsAdmin())
            {
                Console.WriteLine("Asking Windows for administrator rights...");
                string err = ReceiverAdmin.OpenElevated("--admin");
                if (err != null) Console.Error.WriteLine(err);
                return err == null ? 0 : 1;
            }
            if (!simulate) Log.Init(Path.Combine(HostOptions.DefaultDataDir(), "logs"));
            // well away from 8792, which the service it can install is going to need
            if (port == 0) port = Launcher.FreePort(8893, 20, Launcher.ListeningPorts());
            WebServer web = new WebServer(new Store(), port);
            web.ForceLocal = true;
            web.DashboardOverride = dashboard;
            web.Admin = simulate
                ? new ReceiverAdmin(new SimulatedReceiverOps(), true, "simulated")
                : new ReceiverAdmin(new WindowsReceiverOps(), false, ReceiverAdmin.NewToken());
            web.Start();
            string url = "http://localhost:" + port + "/?admin=" + web.Admin.Token;
            Log.Info("CubeScope " + Version + " Admin page opened on port " + port + (simulate ? " (simulated)" : ""));
            Console.WriteLine("CubeScope " + Version + " Admin page: " + url);
            Console.WriteLine("Keep this window open while you use the page. Close it when you are done.");
            if (open)
            {
                try { Process.Start(url); }
                catch (Exception) { }
            }
            ManualResetEvent quit = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { e.Cancel = true; quit.Set(); };
            quit.WaitOne();
            web.Stop();
            return 0;
        }

        /// <summary>
        /// What the Admin page's scheduled task runs (as SYSTEM) when detailed SFTP logging is due to end: puts OpenSSH back,
        /// saves the session's lines and removes the task. Also works by hand from an administrator prompt.
        /// </summary>
        static int SftpLoggingOff()
        {
            if (!CubeScopeService.IsAdmin())
            {
                Console.Error.WriteLine("--sftp-logging-off needs administrator rights (the Admin page's scheduled task runs it as SYSTEM).");
                return 1;
            }
            Log.Init(Path.Combine(HostOptions.DefaultDataDir(), "logs"));
            Log.Info("CubeScope " + Version + " --sftp-logging-off");
            bool busy;
            ReceiverReview r = new ReceiverAdmin(new WindowsReceiverOps(), false, "task").LoggingOff("the time set for it is up", out busy);
            bool failed = false;
            foreach (string n in r.Notes) Console.WriteLine(n);
            foreach (ReceiverStep s in r.Steps)
            {
                Console.WriteLine("  " + (s.Status ?? "") + ": " + s.Text + (s.Detail.Length > 0 ? " - " + s.Detail : ""));
                if (s.Status == "failed") failed = true;
            }
            return failed ? 1 : 0;
        }

        static int Run(string[] args)
        {
            if (args.Length == 0) { Usage(); return 1; }
            if (args[0] == "--admin" || args[0] == "--admin-simulate") return RunAdmin(args);
            if (args[0] == "--sftp-logging-off") return SftpLoggingOff();
            if (args[0] == "--admin-probe")
            {
                // what the Admin page reads, without changing anything
                Console.WriteLine(new ReceiverAdmin(new WindowsReceiverOps(), false, "probe").StateJson());
                return 0;
            }
            LoadOptions o = new LoadOptions();
            string fromS = null, toS = null, filterS = "", trace = null, csv = null, report = null, rulesPath = null;
            string bucketS = null, dashboard = null, serverZone = null, zoneS = null;
            bool serve = false, open = false, live = true, summary = false, json = false;
            bool localOnly = false, alarms = false, install = false;
            string dataDir = null, alarmEventFile = null;
            DateTime alarmAt = DateTime.MinValue;
            int rebuildMinutes = 0;
            int port = 8792, httpsPort = 0;
            string allowGroup = RemoteSettings.DefaultGroup;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "--logs": o.Root = PathArg(args, ref i); break;
                    case "--cube": o.Cubes.Add(Arg(args, ref i)); break;
                    case "--days": o.Days = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--hours":
                        o.Hours = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture);
                        if (o.Hours < 1) { Console.Error.WriteLine("--hours is 1 or more"); return 1; }
                        break;
                    case "--local-only": localOnly = true; break;
                    case "--alarms": alarms = true; break;
                    case "--data-dir": dataDir = PathArg(args, ref i); break;
                    case "--alarm-event-file": alarmEventFile = PathArg(args, ref i); break;
                    case "--rebuild-minutes": rebuildMinutes = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--alarm-at":
                    {
                        long ms = WebServer.ParseTime(Arg(args, ref i));
                        if (ms < 0) { Console.Error.WriteLine("--alarm-at is yyyy-MM-ddTHH:mm (UTC)"); return 1; }
                        alarmAt = TimeUtil.ToDate(ms);
                        break;
                    }
                    case "--install-service": install = true; break;
                    case "--uninstall-service": return CubeScopeService.Uninstall();
                    case "--all": o.Days = 0; break;
                    case "--from": fromS = Arg(args, ref i); break;
                    case "--to": toS = Arg(args, ref i); break;
                    case "--pattern": o.Pattern = Arg(args, ref i); break;
                    case "--rules": rulesPath = PathArg(args, ref i); break;
                    case "--threads": o.Threads = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--server-zone": serverZone = Arg(args, ref i); break;
                    case "--zone": zoneS = Arg(args, ref i); break;
                    case "--serve":
                        serve = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            port = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "--open": open = true; break;
                    case "--no-live": live = false; break;
                    case "--summary": summary = true; break;
                    case "--bucket": bucketS = Arg(args, ref i); break;
                    case "--filter": filterS = Arg(args, ref i); break;
                    case "--trace": trace = Arg(args, ref i); break;
                    case "--csv": csv = PathArg(args, ref i); break;
                    case "--report": report = PathArg(args, ref i); break;
                    case "--json": json = true; break;
                    case "--dashboard": dashboard = PathArg(args, ref i); break;
                    case "--https": httpsPort = int.Parse(Arg(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--allow-group": allowGroup = Arg(args, ref i); break;
                    case "--rules-sample": Console.Write(Rules.DefaultText); return 0;
                    case "--selftest": return SelfTest.Run();
                    case "--pickcheck":
                    {
                        string folder = i + 1 < args.Length ? Arg(args, ref i) : Environment.CurrentDirectory;
                        List<string> problems = Launcher.CheckDialogPlumbing(folder);
                        if (problems.Count == 0) { Console.WriteLine("folder window plumbing OK"); return 0; }
                        for (int k = 0; k < problems.Count; k++) Console.WriteLine("  " + problems[k]);
                        return 1;
                    }
                    case "--help": case "-h": case "/?": Usage(); return 0;
                    default:
                        Console.Error.WriteLine("unknown option " + a + " (--help lists them)");
                        return 1;
                }
            }
            if (string.IsNullOrEmpty(o.Root)) { Console.Error.WriteLine("--logs <folder> is required (--help lists every option)"); return 1; }

            // rules
            Rules rules = LoadRules(rulesPath);

            QueryParams q = new QueryParams();
            if (fromS != null)
            {
                q.From = WebServer.ParseTime(fromS);
                if (q.From < 0) { Console.Error.WriteLine("--from must be yyyy-MM-dd or yyyy-MM-ddTHH:mm"); return 1; }
            }
            if (toS != null)
            {
                q.To = WebServer.ParseTime(toS);
                if (q.To < 0) { Console.Error.WriteLine("--to must be yyyy-MM-dd or yyyy-MM-ddTHH:mm"); return 1; }
                if (toS.Length == 10) q.To += 86400000L;           // a date alone means the whole day
            }
            o.FromMs = q.From; o.ToMs = q.To;
            q.Filter = Filter.Parse(filterS);
            if (q.Filter.Error != null) { Console.Error.WriteLine("filter: " + q.Filter.Error); return 1; }
            q.Bucket = WebServer.ParseBucket(bucketS);
            if (bucketS != null && q.Bucket == 0 && bucketS != "auto") { Console.Error.WriteLine("--bucket is 1m 5m 15m 30m 1h 3h 6h or 1d"); return 1; }
            if (serverZone != null)
            {
                TimeZoneInfo tzi; double fh;
                if (!Clock.ParseZone(serverZone, out tzi, out fh))
                {
                    Console.Error.WriteLine("--server-zone is UTC, UTC-6, UTC+5:30, local, or a Windows zone name such as \"Mountain Standard Time\"");
                    return 1;
                }
            }
            if (zoneS != null)
            {
                if (!ZoneMap.Valid(zoneS)) { Console.Error.WriteLine("--zone is cube, server, UTC, UTC-6, local, or a Windows zone name"); return 1; }
                q.Zone = zoneS;
                // the range is read on that clock: files a day either side are read anyway
            }

            HostOptions h = new HostOptions();
            h.Load = o; h.Rules = rules; h.Port = port; h.Dashboard = dashboard; h.ServerZone = serverZone; h.Live = live;
            h.LocalOnly = localOnly; h.Alarms = alarms; h.AlarmEventFile = alarmEventFile; h.AlarmAt = alarmAt; h.RebuildMinutes = rebuildMinutes;
            h.HttpsPort = httpsPort; h.AllowGroup = allowGroup;
            if (dataDir != null) h.DataDir = dataDir;
            if (install)
            {
                if (o.Hours <= 0) o.Hours = 24;
                return CubeScopeService.Install(h, rulesPath);
            }

            Console.Error.WriteLine("CubeScope " + Version + " reading " + o.Root);
            Store st;
            double loadSec;
            Host host = null;
            if (serve)
            {
                // the same host the service runs: reads, follows, serves, and with --alarms judges
                if (alarms || o.Hours > 0) Log.Init(Path.Combine(h.DataDir, "logs"));
                host = new Host(h);
                host.Start();
                st = host.Store;
                loadSec = host.LoadSeconds;
            }
            else
            {
                Stopwatch sw = Stopwatch.StartNew();
                o.Live = false;
                st = Loader.LoadAll(o, rules);
                st.ServerZoneId = serverZone;
                loadSec = sw.Elapsed.TotalSeconds;
                long managed, priv, ws;
                MemoryGuard.Sample(out managed, out priv, out ws);
                long legs = 0;
                lock (st.Sync) for (int i = 0; i < st.Sources.Count; i++) legs += st.Sources[i].LegsAdded;
                Console.Error.WriteLine("  " + legs.ToString("N0", CultureInfo.InvariantCulture) + " legs in " +
                                        loadSec.ToString("0.0", CultureInfo.InvariantCulture) + " s; memory in use " +
                                        MemoryGuard.MB(managed) + " (process " + MemoryGuard.MB(priv) + ")");
            }
            if (st.Sources.Count == 0)
            {
                Console.Error.WriteLine("No log files under " + o.Root + " match " + o.Pattern + ".");
                if (host != null) host.Stop();
                return 1;
            }

            bool did = false;
            if (trace != null) { Report.Trace(st, trace, q, Console.Out, 20); did = true; }
            if (csv != null)
            {
                QueryResult r;
                lock (st.Sync) r = Query.Run(st, q);
                using (StreamWriter w = new StreamWriter(csv, false, new UTF8Encoding(false)))
                    lock (st.Sync) Report.Csv(st, Query.Sorted(r.Matched, "start", false, r.Zone), w, r.Zone);
                Console.Error.WriteLine("wrote " + r.Matched.Count.ToString("N0", CultureInfo.InvariantCulture) + " calls to " + csv);
                did = true;
            }
            if (report != null)
            {
                QueryResult r;
                lock (st.Sync) r = Query.Run(st, q);
                File.WriteAllText(report, Report.StaticHtml(st, q, r, "CubeScope report"), new UTF8Encoding(false));
                Console.Error.WriteLine("wrote " + report);
                did = true;
            }
            if (json)
            {
                QueryResult r;
                lock (st.Sync) { r = Query.Run(st, q); Console.Out.Write(Query.StateJson(st, q, r, "")); }
                did = true;
            }
            if (summary || (!did && !serve)) Report.Summary(st, q, Console.Out, loadSec);
            if (!serve) return 0;

            string url = "http://localhost:" + port + "/";
            Console.WriteLine("CubeScope " + Version + " serving " + url + (live ? " - following new records" : ""));
            Console.WriteLine("Press Ctrl+C to stop.");
            if (open)
            {
                try { Process.Start(url); }
                catch (Exception) { }
            }
            ManualResetEvent quit = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate (object s, ConsoleCancelEventArgs e) { e.Cancel = true; quit.Set(); };
            quit.WaitOne();
            host.Stop();
            return 0;
        }
    }
}
