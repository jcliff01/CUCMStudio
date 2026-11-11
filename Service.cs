using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace CubeScope
{
    /// <summary>What a long-running CubeScope serves with: the console's --serve and the service share it.</summary>
    public sealed class HostOptions
    {
        public LoadOptions Load = new LoadOptions();
        public Rules Rules;
        public int Port = 8792;
        public string Dashboard, ServerZone;
        public bool Live = true, LocalOnly, Alarms;
        public string DataDir = DefaultDataDir();
        public string AlarmEventFile;     // testing: alarm events to a file, not the Application log
        public DateTime AlarmAt = DateTime.MinValue;   // testing: judge the window ending then (UTC), not now
        public int RebuildMinutes;                     // testing: read the window afresh this often (0: Hours/4)
        public int HttpsPort;                          // remote access over HTTPS with a Windows sign-in; 0 = off
        public string AllowGroup = RemoteSettings.DefaultGroup;

        public static string DefaultDataDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CubeScope");
        }
    }

    /// <summary>
    /// Reads the logs, serves the dashboard, follows new records and, with Alarms, runs the alarm engine.
    /// With Load.Hours set (the service: 24) it keeps only that many hours of each CUBE's files and reads
    /// that window afresh every Hours/4 hours (at least hourly), swapping the new store in under the web
    /// server and the alarms, so memory stays flat however long it runs. The new store is read and its
    /// tailer started before the swap, so no record is missed between the two.
    /// </summary>
    public sealed class Host
    {
        readonly HostOptions _h;
        readonly object _swapLock = new object();
        volatile Store _st;
        volatile Tailer _tail;
        WebServer _web;
        ConfigHistory _configs;
        AlarmEngine _alarms;
        Timer _rebuild;
        int _rebuilding;
        public double LoadSeconds;

        readonly CubeLinks _links;

        public Host(HostOptions h)
        {
            _h = h;
            _links = new CubeLinks(Path.Combine(h.DataDir, "cube-links.json"));
            _h.Load.Links = _links;
        }

        Tailer NewTailer(Store st, LoadOptions o)
        {
            Tailer t = new Tailer(st, o);
            t.NewLink = delegate () { ThreadPool.QueueUserWorkItem(delegate (object x) { Rebuild(); }); };
            t.Start();
            return t;
        }

        public Store Store { get { return _st; } }
        public WebServer Web { get { return _web; } }
        public AlarmEngine Alarms { get { return _alarms; } }
        public ConfigHistory Configs { get { return _configs; } }

        LoadOptions CopyLoad()
        {
            LoadOptions s = _h.Load, o = new LoadOptions();
            o.Root = s.Root; o.Pattern = s.Pattern; o.Cubes = new List<string>(s.Cubes); o.Days = s.Days; o.Hours = s.Hours;
            o.FromMs = s.FromMs; o.ToMs = s.ToMs; o.Threads = s.Threads; o.Quiet = s.Quiet; o.Live = _h.Live; o.Links = _links;
            return o;
        }

        Store Read(out LoadOptions o, out double seconds)
        {
            o = CopyLoad();
            Stopwatch sw = Stopwatch.StartNew();
            Store st = Loader.LoadAll(o, _h.Rules);
            st.ServerZoneId = _h.ServerZone;
            seconds = sw.Elapsed.TotalSeconds;
            long managed, priv, ws;
            MemoryGuard.Sample(out managed, out priv, out ws);
            long legs = 0;
            lock (st.Sync) for (int i = 0; i < st.Sources.Count; i++) legs += st.Sources[i].LegsAdded;
            Log.Info("read " + st.Sources.Count + " CUBE(s), " + legs.ToString("N0", CultureInfo.InvariantCulture) + " legs in " +
                     seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s; memory " + MemoryGuard.MB(managed) + " (process " + MemoryGuard.MB(priv) + ")");
            return st;
        }

        /// <summary>Reads the logs and starts serving. Returns once the dashboard answers.</summary>
        public void Start()
        {
            Log.Info("CubeScope " + Program.Version + " starting: logs " + _h.Load.Root + ", port " + _h.Port +
                     (_h.Load.Hours > 0 ? ", keeping " + _h.Load.Hours + " h" : _h.Load.Days > 0 ? ", " + _h.Load.Days + " day(s)" : ", every file") +
                     (_h.LocalOnly ? ", this computer only" : "") + (_h.Alarms ? ", alarms in " + _h.DataDir : "") +
                     ", user " + Environment.UserDomainName + "\\" + Environment.UserName);
            LoadOptions o;
            double sec;
            _st = Read(out o, out sec);
            LoadSeconds = sec;
            if (_st.Sources.Count == 0) Log.Warn("no log files under " + _h.Load.Root + " match " + _h.Load.Pattern);
            if (_h.Live) _tail = NewTailer(_st, o);

            // the service forgets records older than its window: keep the newest configuration changes in a file
            _configs = new ConfigHistory(_h.Load.Hours > 0 ? Path.Combine(_h.DataDir, "config-history.json") : null);
            _configs.Load();
            _configs.Merge(_st);

            _web = new WebServer(_st, _h.Port);
            _web.Configs = _configs;
            _web.DashboardOverride = _h.Dashboard;
            _web.ForceLocal = _h.LocalOnly;
            _web.Following = _h.Live;
            _web.Options = _h;
            _web.HttpsPort = _h.HttpsPort;
            _web.AllowGroup = _h.AllowGroup;
            _web.Status = delegate ()
            {
                Tailer t = _tail;
                return t != null ? t.Status : "not following new records (--no-live)";
            };
            if (_h.Alarms)
            {
                _alarms = new AlarmEngine(_h.DataDir, delegate () { return _st; });
                _alarms.EventFile = _h.AlarmEventFile;
                _alarms.CdrRoot = _h.Load.Root;
                _alarms.SyslogFeed = _h.Load;
                if (_h.AlarmAt != DateTime.MinValue)
                {
                    DateTime at = _h.AlarmAt;
                    _alarms.UtcNow = delegate () { return at; };
                    Log.Warn("alarms judge the window ending " + at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC, not now (--alarm-at, for testing)");
                }
                _alarms.Load();
                _alarms.Start();
                _web.Alarms = _alarms;
                AlarmConfig c = _alarms.Config;
                Log.Info("alarms: " + (c.Enabled ? "on" : "off") + ", " + c.Rules.Count + " rule(s), settings " + _alarms.ConfigFile);
            }
            _web.Start();
            Log.Info("serving http://localhost:" + _h.Port + "/" + (_web.LocalOnly ? " (this computer only)" : ""));

            if (_h.Load.Hours > 0 && _h.Live)
            {
                int every = _h.RebuildMinutes > 0 ? _h.RebuildMinutes * 60000 : Math.Max(1, _h.Load.Hours / 4) * 3600000;
                _rebuild = new Timer(delegate (object x) { Rebuild(); }, null, every, every);
            }
        }

        /// <summary>Reads the window afresh and swaps it in (the service's way of forgetting old records).</summary>
        public void Rebuild()
        {
            if (Interlocked.Exchange(ref _rebuilding, 1) == 1) return;
            try
            {
                Log.Info(_h.Load.Hours > 0 ? "reading the newest " + _h.Load.Hours + " h afresh" : "reading the logs afresh");
                LoadOptions o;
                double sec;
                Store ns = Read(out o, out sec);
                Tailer nt = null;
                if (_h.Live) nt = NewTailer(ns, o);
                _configs.Merge(_st);    // before the old window's records go
                Tailer old;
                lock (_swapLock)
                {
                    old = _tail;
                    _st = ns;
                    _tail = nt;
                    _web.SwapStore(ns);
                }
                if (old != null) old.Stop();
                MemoryGuard.Breathe();
                long managed, priv, ws;
                MemoryGuard.Sample(out managed, out priv, out ws);
                Log.Info("swapped in the fresh window; memory now " + MemoryGuard.MB(managed) + " (process " + MemoryGuard.MB(priv) + ")");
            }
            catch (Exception ex) { Log.Error("reading the window afresh failed; still serving the old one", ex); }
            finally { Interlocked.Exchange(ref _rebuilding, 0); }
        }

        public void Stop()
        {
            Log.Info("stopping");
            if (_rebuild != null) _rebuild.Dispose();
            if (_alarms != null) _alarms.Stop();
            if (_web != null) _web.Stop();
            Tailer t = _tail;
            if (t != null) t.Stop();
            if (_configs != null && _st != null) _configs.Merge(_st);
        }
    }

    /// <summary>
    /// CubeScope as a Windows service ("CubeScope"): starts with Windows, runs as LocalSystem, keeps
    /// the newest 24 hours, answers this computer only, and runs the alarms. Its arguments are the
    /// ones --install-service wrote into the service's command line. Logs to %ProgramData%\CubeScope\logs.
    /// </summary>
    public sealed class CubeScopeService : ServiceBase
    {
        public const string Name = "CubeScope";
        readonly string[] _args;
        Host _host;

        public CubeScopeService(string[] args)
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
            _args = args;
        }

        protected override void OnStart(string[] startArgs)
        {
            // Windows waits only about 30 s for OnStart: read the logs on a thread of our own
            Thread t = new Thread(delegate ()
            {
                try
                {
                    string err;
                    HostOptions h = Program.ParseHost(_args, out err);
                    if (h == null) { Log.Warn("service arguments not usable: " + err); ExitCode = 87; Stop(); return; }
                    // remote access, as the Admin page last set it (no file: this computer only)
                    RemoteSettings rs = RemoteSettings.Load(h.DataDir, h.Port);
                    if (rs != null && h.HttpsPort == 0) { h.HttpsPort = rs.HttpsPort; h.AllowGroup = rs.Group; }
                    _host = new Host(h);
                    _host.Start();
                }
                catch (Exception ex)
                {
                    Log.Error("the service could not start", ex);
                    ExitCode = 1;
                    try { Stop(); } catch (Exception) { }
                }
            });
            t.IsBackground = true;
            t.Name = "start";
            t.Start();
        }

        protected override void OnStop() { if (_host != null) _host.Stop(); }
        protected override void OnShutdown() { OnStop(); }

        // ------------------------------------------------------------ install / uninstall (sc.exe)

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        static string Q(string s) { return "\\\"" + s.TrimEnd('\\') + "\\\""; }   // a quoted value inside binPath= "..."

        /// <summary>The service's command line: the exe, --service, then the settings it runs with.</summary>
        public static string BinPath(string exe, HostOptions h, string rulesPath)
        {
            StringBuilder b = new StringBuilder();
            b.Append(Q(exe)).Append(" --service --logs ").Append(Q(h.Load.Root)).Append(" --serve ").Append(h.Port)
             .Append(" --hours ").Append(h.Load.Hours).Append(" --local-only --alarms");
            if (h.DataDir != HostOptions.DefaultDataDir()) b.Append(" --data-dir ").Append(Q(h.DataDir));
            if (!string.IsNullOrEmpty(rulesPath)) b.Append(" --rules ").Append(Q(rulesPath));
            if (!string.IsNullOrEmpty(h.ServerZone)) b.Append(" --server-zone ").Append(Q(h.ServerZone));
            if (h.Load.Pattern != "*.log") b.Append(" --pattern ").Append(Q(h.Load.Pattern));
            foreach (string c in h.Load.Cubes) b.Append(" --cube ").Append(Q(c));
            return b.ToString();
        }

        static int Sc(string args, bool quiet)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (!quiet || p.ExitCode != 0) Console.WriteLine("  sc " + args.Split(' ')[0] + ": " + (p.ExitCode == 0 ? "ok" : "exit " + p.ExitCode + " " + o.Trim()));
                return p.ExitCode;
            }
        }

        static bool Exists()
        {
            foreach (ServiceController s in ServiceController.GetServices())
                if (string.Equals(s.ServiceName, Name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void StopIfRunning()
        {
            try
            {
                using (ServiceController s = new ServiceController(Name))
                {
                    if (s.Status != ServiceControllerStatus.Stopped)
                    {
                        Console.WriteLine("  stopping the running service ...");
                        s.Stop();
                        s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("  could not stop it: " + ex.Message); }
        }

        /// <summary>Creates (or updates) and starts the service. Needs an administrator.</summary>
        public static int Install(HostOptions h, string rulesPath)
        {
            if (!IsAdmin()) { Console.Error.WriteLine("--install-service needs an administrator command prompt."); return 1; }
            if (!Directory.Exists(h.Load.Root)) { Console.Error.WriteLine("no such folder: " + h.Load.Root); return 1; }
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string bin = BinPath(exe, h, rulesPath);
            Console.WriteLine("Installing the CubeScope service:");
            Console.WriteLine("  " + bin.Replace("\\\"", "\""));
            Directory.CreateDirectory(h.DataDir);
            try
            {
                // the alarms' event source; registering one needs an administrator, which we are now
                string src = new AlarmConfig().EventSource;
                if (!EventLog.SourceExists(src)) { EventLog.CreateEventSource(src, "Application"); Console.WriteLine("  registered event source '" + src + "'"); }
            }
            catch (Exception ex) { Console.WriteLine("  could not register the event source: " + ex.Message); }
            int rc;
            if (Exists())
            {
                StopIfRunning();
                rc = Sc("config " + Name + " binPath= \"" + bin + "\" start= delayed-auto obj= LocalSystem DisplayName= \"CubeScope\"", false);
            }
            else rc = Sc("create " + Name + " binPath= \"" + bin + "\" start= delayed-auto obj= LocalSystem DisplayName= \"CubeScope\"", false);
            if (rc != 0) return 1;
            Sc("description " + Name + " \"CubeScope: the CUBE call-accounting dashboard on http://localhost:" + h.Port +
               "/ (this computer only) and its DNIS alarms. Log: " + Path.Combine(h.DataDir, "logs") + "\"", true);
            Sc("failure " + Name + " reset= 86400 actions= restart/60000/restart/60000/restart/300000", true);
            rc = Sc("start " + Name, false);
            Console.WriteLine(rc == 0
                ? "Started. It reads the newest " + h.Load.Hours + " hours first (a few seconds to a minute), then http://localhost:" + h.Port + "/ answers on this computer."
                : "Installed but not started; see Services (services.msc) and the log in " + Path.Combine(h.DataDir, "logs") + ".");
            Console.WriteLine("Alarms: open the dashboard and press Alarms. Remove with: CubeScope.exe --uninstall-service");
            return rc == 0 ? 0 : 1;
        }

        public static int Uninstall()
        {
            if (!IsAdmin()) { Console.Error.WriteLine("--uninstall-service needs an administrator command prompt."); return 1; }
            if (!Exists()) { Console.WriteLine("The CubeScope service is not installed."); return 0; }
            StopIfRunning();
            int rc = Sc("delete " + Name, false);
            Console.WriteLine(rc == 0 ? "Removed. Its settings and log stay in " + HostOptions.DefaultDataDir() + "." : "Not removed.");
            return rc == 0 ? 0 : 1;
        }
    }
}
