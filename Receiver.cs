using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CubeScope
{
    /// <summary>
    /// The Admin page's work: sets this Windows server up to receive CUBE call record files over SFTP,
    /// using Windows' OpenSSH Server. Only a window opened with administrator rights (--admin) can change
    /// anything, and only with the key that window's browser link carries: the service runs as SYSTEM and
    /// answers anyone logged on to this computer, so it must never do this work for them.
    /// </summary>
    public sealed class ReceiverSettings
    {
        public int Port = 2121;
        public string Folder = @"C:\CubeCDR";
        public string Account = "cube-cdr";
        /// <summary>Blank keeps an existing account's password. Never written to the log or sent back.</summary>
        public string Password = "";
        public List<string> Allowed = new List<string>();
        /// <summary>Also run CubeScope as a Windows service reading this folder, for the alarms and the cleanup.</summary>
        public bool RunService;

        static readonly Regex AccountName = new Regex(@"^[a-z][a-z0-9_-]{0,19}$", RegexOptions.CultureInvariant);
        static readonly Regex Ipv4 = new Regex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?:/(\d{1,2}))?$", RegexOptions.CultureInvariant);
        static readonly string[] Reserved = { "administrator", "guest", "defaultaccount", "wdagutilityaccount", "krbtgt", "system" };

        public static bool IsReserved(string account) { return Array.IndexOf(Reserved, account) >= 0; }

        static string Text(Dictionary<string, object> o, string k)
        {
            object v;
            if (!o.TryGetValue(k, out v)) return "";
            return v as string ?? "";
        }

        public static ReceiverSettings FromJson(Dictionary<string, object> o, out string error)
        {
            error = null;
            if (o == null) { error = "The settings are not readable."; return null; }
            ReceiverSettings s = new ReceiverSettings();
            object v;
            double d = o.TryGetValue("port", out v) && v is double ? (double)v : 0;
            if (d != Math.Floor(d) || d < 1 || d > 65535) { error = "The port must be a whole number from 1 to 65535."; return null; }
            s.Port = (int)d;
            // OpenSSH for Windows matches names in lower case
            s.Account = Text(o, "account").Trim().ToLowerInvariant();
            if (!AccountName.IsMatch(s.Account))
            {
                error = "The account name must start with a letter and use only letters, numbers, - and _ (20 characters at most).";
                return null;
            }
            if (IsReserved(s.Account)) { error = "Windows keeps the account name " + s.Account + " for itself. Choose another."; return null; }
            s.Password = Text(o, "password");
            if (s.Password.Length > 127) { error = "The password is too long (127 characters at most)."; return null; }
            for (int i = 0; i < s.Password.Length; i++)
            {
                char c = s.Password[i];
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '?')
                {
                    error = "The password cannot contain spaces or question marks, because the CUBE cannot take them.";
                    return null;
                }
            }
            s.Folder = CleanFolder(Text(o, "folder"), out error);
            if (s.Folder == null) return null;
            if (!ParseAllowed(Text(o, "allowed"), s.Allowed, out error)) return null;
            s.RunService = o.TryGetValue("service", out v) && v is bool && (bool)v;
            return s;
        }

        /// <summary>The folder as a full local path, or null with the reason. It ends up in OpenSSH's settings
        /// and the account gets write access to it, so system folders are refused.</summary>
        public static string CleanFolder(string f, out string error)
        {
            error = null;
            f = (f ?? "").Trim().TrimEnd('\\');
            if (!Regex.IsMatch(f, @"^[A-Za-z]:\\[^""<>|*?/:\r\n]+$") || f.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                error = @"The folder must be a full path on this server, below the top of a drive, such as C:\CubeCDR.";
                return null;
            }
            string full = Path.GetFullPath(f).TrimEnd('\\');
            string[] system = {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh") };
            for (int i = 0; i < system.Length; i++)
            {
                if (system[i].Length == 0) continue;
                if (full.Equals(system[i], StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(system[i] + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Choose a folder outside " + system[i] + ".";
                    return null;
                }
            }
            return full;
        }

        /// <summary>Addresses or subnets, separated by commas or spaces. At least one; "anywhere" is refused.</summary>
        public static bool ParseAllowed(string text, List<string> into, out string error)
        {
            error = null;
            string[] parts = (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i], clean = null;
                Match m = Ipv4.Match(t);
                if (m.Success)
                {
                    bool ok = true;
                    for (int g = 1; g <= 4; g++) if (int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) > 255) ok = false;
                    string addr = m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value + "." + m.Groups[4].Value;
                    int bits = m.Groups[5].Success ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 32;
                    if (bits < 1 || bits > 32 || addr == "0.0.0.0") ok = false;
                    if (ok) clean = bits == 32 ? addr : addr + "/" + bits;
                }
                else if (t.IndexOf(':') >= 0)
                {
                    string[] a = t.Split('/');
                    IPAddress ip; int bits = 128;
                    if (a.Length <= 2 && IPAddress.TryParse(a[0], out ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                        (a.Length == 1 || (int.TryParse(a[1], NumberStyles.None, CultureInfo.InvariantCulture, out bits) && bits >= 1 && bits <= 128)))
                        clean = bits == 128 ? a[0] : a[0] + "/" + bits;
                }
                if (clean == null) { error = "\"" + t + "\" is not a CUBE address."; return false; }
                if (!into.Contains(clean)) into.Add(clean);
            }
            if (into.Count == 0) { error = "Enter the address of at least one CUBE, so only your CUBEs can connect."; return false; }
            return true;
        }
    }

    /// <summary>Reads and changes OpenSSH's settings file as text; nothing here touches the computer.</summary>
    public static class SshdConfig
    {
        public const string Marker = "# Added by CubeScope: this account can only transfer files, inside one folder";
        const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        static readonly Regex MatchLine = new Regex(@"^\s*Match\s", Opt);
        static readonly Regex PortLine = new Regex(@"^\s*Port\s+(\d+)\s*(#.*)?$", Opt);
        static readonly Regex OffPortLine = new Regex(@"^\s*#\s*Port\s+\d+\s*$", Opt);
        static readonly Regex UserLine = new Regex(@"^\s*Match\s+User\s+""?([^\s""]+)""?\s*$", Opt);
        static readonly Regex ChrootLine = new Regex(@"^\s*ChrootDirectory\s+""?([^""]+?)""?\s*$", Opt);
        static readonly Regex LimitLine = new Regex(@"^\s*(AllowUsers|AllowGroups|DenyUsers|DenyGroups)\s", Opt);

        static List<string> Split(string text, out string nl)
        {
            nl = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            return new List<string>(text.Replace("\r\n", "\n").Split('\n'));
        }

        static int FirstMatch(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++) if (MatchLine.IsMatch(lines[i])) return i;
            return lines.Count;
        }

        /// <summary>The port set for the whole server, or 0 when the file leaves it at the default of 22.</summary>
        public static int ActivePort(string text)
        {
            string nl;
            List<string> ls = Split(text, out nl);
            int end = FirstMatch(ls);
            for (int i = 0; i < end; i++)
            {
                Match m = PortLine.Match(ls[i]);
                int p;
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out p)) return p;
            }
            return 0;
        }

        /// <summary>The first account limited to one folder by a Match User block.</summary>
        public static bool FindBlock(string text, out string account, out string folder)
        {
            account = null; folder = null;
            string nl;
            List<string> ls = Split(text, out nl);
            for (int i = 0; i < ls.Count; i++)
            {
                Match u = UserLine.Match(ls[i]);
                if (!u.Success) continue;
                for (int j = i + 1; j < ls.Count && !MatchLine.IsMatch(ls[j]); j++)
                {
                    Match c = ChrootLine.Match(ls[j]);
                    if (c.Success) { account = u.Groups[1].Value; folder = c.Groups[1].Value; return true; }
                }
            }
            return false;
        }

        /// <summary>Things in the file that could stop this account signing in.</summary>
        public static List<string> Warnings(string text)
        {
            List<string> w = new List<string>();
            string nl;
            List<string> ls = Split(text, out nl);
            for (int i = 0; i < ls.Count; i++)
                if (LimitLine.IsMatch(ls[i]))
                    w.Add("OpenSSH's settings limit who can sign in (" + ls[i].Trim() + "). Make sure this account is allowed.");
            return w;
        }

        /// <summary>The file with the port set and the account's block at the end (replacing any it had).
        /// Doing it twice gives the same text. Line endings are kept.</summary>
        public static string Update(string text, int port, string account, string folder)
        {
            string nl;
            List<string> ls = Split(text, out nl);

            // an account's block runs to the next Match line, or to the end of the file
            for (int i = 0; i < ls.Count; i++)
            {
                Match u = UserLine.Match(ls[i]);
                if (!u.Success || !u.Groups[1].Value.Equals(account, StringComparison.OrdinalIgnoreCase)) continue;
                int end = i + 1;
                while (end < ls.Count && !MatchLine.IsMatch(ls[end])) end++;
                int start = i > 0 && ls[i - 1].Trim() == Marker ? i - 1 : i;
                ls.RemoveRange(start, end - start);
                i = start - 1;
            }

            // the port, only in the part of the file that applies to the whole server
            int globals = FirstMatch(ls);
            int first = -1;
            for (int i = 0; i < globals; i++)
            {
                if (!PortLine.IsMatch(ls[i])) continue;
                if (first < 0) { first = i; ls[i] = "Port " + port.ToString(CultureInfo.InvariantCulture); }
                else { ls.RemoveAt(i); i--; globals--; }
            }
            if (first < 0)
            {
                int off = -1;
                for (int i = 0; i < globals && off < 0; i++) if (OffPortLine.IsMatch(ls[i])) off = i;
                if (off >= 0) ls[off] = "Port " + port.ToString(CultureInfo.InvariantCulture);
                else ls.Insert(0, "Port " + port.ToString(CultureInfo.InvariantCulture));
            }

            while (ls.Count > 0 && ls[ls.Count - 1].Trim().Length == 0) ls.RemoveAt(ls.Count - 1);
            ls.Add("");
            ls.Add(Marker);
            ls.Add("Match User " + account);
            ls.Add("    ChrootDirectory " + (folder.IndexOf(' ') >= 0 ? "\"" + folder + "\"" : folder));
            // detailed logging, while it is on, survives an Apply
            ls.Add("    ForceCommand internal-sftp" + (SftpLogConfig.IsOn(text) ? " -l " + SftpLogConfig.SftpLevel : ""));
            ls.Add("    PasswordAuthentication yes");
            ls.Add("    AllowTcpForwarding no");
            ls.Add("    X11Forwarding no");
            ls.Add("");
            return string.Join(nl, ls);
        }
    }

    public sealed class ReceiverFile { public string Name; public long Size; public string When; }

    /// <summary>Everything the Admin page does to this computer. For each change: null when done, or the
    /// reason in words. The self-test and --admin-simulate use a stand-in that changes nothing.
    /// The detailed-logging switch's part is ISftpLogOps (SftpLogging.cs).</summary>
    public interface IReceiverOps : ISftpLogOps, IRemoteOps
    {
        string SshdExe();
        string Installer();
        string Install(string msi);
        string ServiceState();
        string StartAuto();
        string Restart();
        string ConfigText();
        string WriteConfig(string text, out string backup);
        string RestoreConfig(string backup);
        string TestConfig(string sshd);
        /// <summary>0 no such account, 1 an ordinary one, 2 an administrator.</summary>
        int AccountKind(string name);
        string CreateAccount(string name, string password);
        string SetPassword(string name, string password);
        bool FolderExists(string path);
        bool FolderAllowed(string path, string account);
        string AllowFolder(string path, string account);
        /// <summary>What answers on the port: null nothing listens, "" it listens and says nothing.</summary>
        string PortBanner(int port);
        /// <summary>The firewall rule's allowed addresses, comma separated, or null when there is no rule.</summary>
        string FirewallRule(out int port);
        string SetFirewall(int port, string[] allowed);
        List<ReceiverFile> NewestFiles(string folder, int max);
        List<string> RecentSignIns(int max);
        List<string> LocalAddresses();
        void Wait(int ms);
        /// <summary>The folder the CubeScope service reads, "" when that cannot be told, null when there is no service.</summary>
        string CubeScopeServiceFolder();
        string InstallCubeScopeService(string folder);
        string ExePath();
    }

    public sealed class ReceiverStep
    {
        public string Text;
        public bool Needed;
        public Func<string> Run;
        public string Status;      // done, skipped, failed, not-run
        public string Detail = "";
    }

    public sealed class ReceiverReview
    {
        public readonly List<string> Blocked = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<ReceiverStep> Steps = new List<ReceiverStep>();
    }

    public sealed partial class ReceiverAdmin
    {
        public readonly IReceiverOps Ops;
        public readonly bool Simulated;
        /// <summary>Random for each window; only the link that window opens in the browser carries it.</summary>
        public readonly string Token;
        int _busy;

        public ReceiverAdmin(IReceiverOps ops, bool simulated, string token) { Ops = ops; Simulated = simulated; Token = token; }

        public static string NewToken()
        {
            byte[] b = new byte[16];
            using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Starts this exe with administrator rights (Windows asks). Null when it started.</summary>
        public static string OpenElevated(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return null;
            }
            catch (Win32Exception ex)
            {
                return ex.NativeErrorCode == 1223 ? "The Windows permission prompt was declined." : ex.Message;
            }
        }

        static bool SameList(string a, List<string> b)
        {
            List<string> x = new List<string>(a.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (x.Count != b.Count) return false;
            for (int i = 0; i < b.Count; i++) if (!x.Contains(b[i])) return false;
            return true;
        }

        static ReceiverStep Step(ReceiverReview r, string text, bool needed, Func<string> run)
        {
            ReceiverStep s = new ReceiverStep();
            s.Text = text; s.Needed = needed; s.Run = run;
            r.Steps.Add(s);
            return s;
        }

        static string Short(string s) { return s.Length > 80 ? s.Substring(0, 80) : s; }

        /// <summary>What Apply would do, from how the computer is now. Changes nothing.</summary>
        public ReceiverReview Review(ReceiverSettings s)
        {
            ReceiverReview r = new ReceiverReview();
            IReceiverOps o = Ops;
            string exe = o.SshdExe();
            string msi = exe == null ? o.Installer() : null;
            string config = o.ConfigText();
            int kind = o.AccountKind(s.Account);
            string banner = o.PortBanner(s.Port);
            bool answering = banner != null && banner.StartsWith("SSH-", StringComparison.Ordinal);
            string portText = s.Port.ToString(CultureInfo.InvariantCulture);

            if (exe == null && msi == null)
                r.Blocked.Add("OpenSSH Server is not installed and its installer is not beside CubeScope. Put the openssh folder next to CubeScope.exe.");
            if (kind == 2) r.Blocked.Add(s.Account + " is an administrator account on this server. Use an ordinary account, because the CUBE's copy of this password is its only protection.");
            if (kind == 0 && s.Password.Length == 0) r.Blocked.Add("Enter a password for the new account " + s.Account + ".");
            if (banner != null && !answering)
                r.Blocked.Add("Port " + portText + " is already in use by another program" + (banner.Length > 0 ? " (it says: " + Short(banner) + ")" : "") + ". Stop that listener or choose another port.");
            if (config != null) r.Notes.AddRange(SshdConfig.Warnings(config));

            Step(r, msi != null ? "Install OpenSSH Server from " + Path.GetFileName(msi) : "OpenSSH Server is installed", exe == null,
                 delegate () { return o.Install(msi); });

            Step(r, config == null ? "Start OpenSSH once, so it creates its settings file" : "OpenSSH's settings file exists", config == null, delegate ()
            {
                string e = o.StartAuto();
                if (e != null) return e;
                for (int i = 0; i < 30 && o.ConfigText() == null; i++) o.Wait(500);
                return o.ConfigText() == null ? "OpenSSH did not create its settings file." : null;
            });

            if (kind == 0) Step(r, "Create the account " + s.Account, true, delegate () { return o.CreateAccount(s.Account, s.Password); });
            else if (s.Password.Length > 0) Step(r, "Change the password of " + s.Account, true, delegate () { return o.SetPassword(s.Account, s.Password); });
            else Step(r, "Keep the account " + s.Account + " and its password", false, null);

            bool exists = o.FolderExists(s.Folder), allowed = exists && kind != 0 && o.FolderAllowed(s.Folder, s.Account);
            Step(r, allowed ? s.Account + " can already add files to " + s.Folder :
                    (exists ? "Let " + s.Account + " add files to " + s.Folder : "Create " + s.Folder + " and let " + s.Account + " add files to it"),
                 !allowed, delegate () { return o.AllowFolder(s.Folder, s.Account); });

            string updated = config == null ? null : SshdConfig.Update(config, s.Port, s.Account, s.Folder);
            bool changeConfig = config == null || updated != config;
            int was = config == null ? 22 : SshdConfig.ActivePort(config);
            Step(r, changeConfig
                    ? "Set the SFTP port to " + portText + (config != null ? " (now " + (was == 0 ? 22 : was).ToString(CultureInfo.InvariantCulture) + ")" : "") +
                      " and limit " + s.Account + " to file transfer inside " + s.Folder + ". The current settings are copied first."
                    : "The port and the file-transfer limit are already set",
                 changeConfig, delegate ()
                 {
                     string cur = o.ConfigText();
                     if (cur == null) return "OpenSSH's settings file is missing.";
                     string next = SshdConfig.Update(cur, s.Port, s.Account, s.Folder);
                     if (next == cur) return null;
                     string backup;
                     string e = o.WriteConfig(next, out backup);
                     if (e != null) return e;
                     string t = o.TestConfig(o.SshdExe());
                     if (t == null) return null;
                     string back = o.RestoreConfig(backup);
                     return "OpenSSH rejected the new settings, so nothing else was changed: " + t +
                            (back == null ? " The old settings were put back." : " Putting the old settings back also failed: " + back + " The copy is " + backup);
                 });

            bool restart = changeConfig || !answering || o.ServiceState() != "Running";
            Step(r, restart ? "Restart OpenSSH and set it to start with Windows" : "OpenSSH is running", restart,
                 delegate () { return o.Restart(); });

            int fwPort;
            string fw = o.FirewallRule(out fwPort);
            bool fwOk = fw != null && fwPort == s.Port && SameList(fw, s.Allowed);
            string list = string.Join(", ", s.Allowed);
            Step(r, fwOk ? "Windows Firewall already allows TCP " + portText + " from " + list
                         : "Allow TCP " + portText + " from " + list + " through Windows Firewall",
                 !fwOk, delegate () { return o.SetFirewall(s.Port, s.Allowed.ToArray()); });

            string svc = o.CubeScopeServiceFolder();
            bool sameFolder = svc != null && svc.Equals(s.Folder, StringComparison.OrdinalIgnoreCase);
            if (svc != null && !sameFolder)
                r.Notes.Add("The CubeScope service already reads " + (svc.Length > 0 ? svc : "another folder") + ", so it is left as it is.");
            if (sameFolder) Step(r, "The CubeScope service already reads " + s.Folder, false, null);
            else if (s.RunService && svc == null)
                Step(r, "Run CubeScope as a Windows service reading " + s.Folder + ", so the dashboard, alarms and cleanup stay on. It will run from " + o.ExePath() + ", so leave the file there.",
                     true, delegate () { return o.InstallCubeScopeService(s.Folder); });

            Step(r, "Check that OpenSSH answers on port " + portText, true, delegate ()
            {
                for (int i = 0; i < 20; i++)
                {
                    string b = o.PortBanner(s.Port);
                    if (b != null && b.StartsWith("SSH-", StringComparison.Ordinal)) return null;
                    o.Wait(500);
                }
                return "OpenSSH is not answering on port " + portText + ".";
            });
            return r;
        }

        /// <summary>Does what Review listed, stopping at the first failure. Null in busy when another run is on.</summary>
        public ReceiverReview Apply(ReceiverSettings s, out bool busy)
        {
            busy = Interlocked.CompareExchange(ref _busy, 1, 0) != 0;
            if (busy) return null;
            try
            {
                ReceiverReview r = Review(s);
                if (r.Blocked.Count > 0) return r;
                Log.Info("admin: setting up the SFTP receiver: port " + s.Port + ", folder " + s.Folder + ", account " + s.Account +
                         ", allowed " + string.Join(",", s.Allowed) + (s.Password.Length > 0 ? ", new password given" : ", password kept"));
                RunSteps(r, false);
                return r;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>Runs the needed steps in order. A failure stops the rest, unless keepGoing (putting things back,
        /// where each step is worth trying on its own).</summary>
        static void RunSteps(ReceiverReview r, bool keepGoing)
        {
            bool failed = false;
            for (int i = 0; i < r.Steps.Count; i++)
            {
                ReceiverStep st = r.Steps[i];
                if (failed && !keepGoing) { st.Status = "not-run"; continue; }
                if (!st.Needed) { st.Status = "skipped"; continue; }
                string e;
                Stopwatch sw = Stopwatch.StartNew();
                try { e = st.Run(); }
                catch (Exception ex) { Log.Error("admin: " + st.Text, ex); e = ex.Message; }
                if (e == null) { st.Status = "done"; Log.Info("admin: done in " + sw.ElapsedMilliseconds + " ms: " + st.Text); }
                else { st.Status = "failed"; st.Detail = e; failed = true; Log.Warn("admin: failed: " + st.Text + ": " + e); }
            }
        }

        static void Check(Json j, string name, bool ok, string text)
        {
            j.Obj().P("name", name).P("ok", ok).P("text", text).EndObj();
        }

        public string StateJson()
        {
            IReceiverOps o = Ops;
            ReceiverSettings s = new ReceiverSettings();
            string config = o.ConfigText();
            bool limited = false;
            if (config != null)
            {
                int p = SshdConfig.ActivePort(config);
                if (p > 0) s.Port = p;
                string acct, folder;
                if (SshdConfig.FindBlock(config, out acct, out folder)) { s.Account = acct; s.Folder = folder; limited = true; }
            }
            int fwPort;
            string fw = o.FirewallRule(out fwPort);
            string exe = o.SshdExe();
            string msi = o.Installer();
            string state = o.ServiceState();
            string banner = o.PortBanner(s.Port);
            bool answering = banner != null && banner.StartsWith("SSH-", StringComparison.Ordinal);
            int kind = o.AccountKind(s.Account);
            bool exists = o.FolderExists(s.Folder), allowed = exists && kind != 0 && o.FolderAllowed(s.Folder, s.Account);
            string portText = s.Port.ToString(CultureInfo.InvariantCulture);

            Json j = new Json(4096);
            j.Obj().P("mode", "admin").P("simulated", Simulated).P("computer", Environment.MachineName).P("logFile", Log.FilePath ?? "");
            j.Key("checks").Arr();
            Check(j, "OpenSSH Server", exe != null, exe != null ? "Installed" :
                  msi != null ? "Not installed. " + Path.GetFileName(msi) + " is ready to install." : "Not installed, and its installer is not beside CubeScope");
            Check(j, "Service", state == "Running", state ?? "Not there yet");
            Check(j, "Port " + portText, answering, answering ? "OpenSSH is answering" :
                  banner != null ? "In use by another program" : "Nothing is answering");
            Check(j, "Account", kind == 1, kind == 0 ? s.Account + " does not exist yet" : kind == 2 ? s.Account + " is an administrator: use another" : s.Account + " exists");
            Check(j, "Folder", allowed, !exists ? s.Folder + " does not exist yet" : allowed ? s.Account + " can add files to " + s.Folder : s.Account + " cannot add files to " + s.Folder + " yet");
            Check(j, "File transfer only", limited, limited ? s.Account + " is limited to file transfer in " + s.Folder : "Not set yet");
            Check(j, "Windows Firewall", fw != null && fwPort == s.Port, fw == null ? "No rule yet" :
                  "TCP " + fwPort.ToString(CultureInfo.InvariantCulture) + " from " + (fw.Length > 0 ? fw : "(could not read the addresses)"));
            string svc = o.CubeScopeServiceFolder();
            Check(j, "CubeScope service", svc != null && svc.Equals(s.Folder, StringComparison.OrdinalIgnoreCase),
                  svc == null ? "Not installed" : "Reads " + (svc.Length > 0 ? svc : "another folder"));
            j.EndArr();
            j.Key("settings").Obj().P("port", s.Port).P("folder", s.Folder).P("account", s.Account).P("allowed", fw ?? "").P("serviceInstalled", svc != null).EndObj();
            j.Key("files").Arr();
            List<ReceiverFile> files = exists ? o.NewestFiles(s.Folder, 8) : new List<ReceiverFile>();
            for (int i = 0; i < files.Count; i++) j.Obj().P("name", files[i].Name).P("size", files[i].Size).P("when", files[i].When).EndObj();
            j.EndArr();
            j.Key("signins").Arr();
            List<string> sign = o.RecentSignIns(8);
            for (int i = 0; i < sign.Count; i++) j.Val(sign[i]);
            j.EndArr();
            j.Key("addresses").Arr();
            List<string> addr = o.LocalAddresses();
            for (int i = 0; i < addr.Count; i++) j.Val(addr[i]);
            j.EndArr();
            WriteLoggingJson(j, config);
            WriteRemoteJson(j, 0);
            j.EndObj();
            return j.ToString();
        }

        static void WriteSteps(Json j, ReceiverReview r)
        {
            j.Key("blocked").Arr();
            for (int i = 0; i < r.Blocked.Count; i++) j.Val(r.Blocked[i]);
            j.EndArr();
            j.Key("notes").Arr();
            for (int i = 0; i < r.Notes.Count; i++) j.Val(r.Notes[i]);
            j.EndArr();
            j.Key("steps").Arr();
            for (int i = 0; i < r.Steps.Count; i++)
            {
                ReceiverStep st = r.Steps[i];
                j.Obj().P("text", st.Text).P("needed", st.Needed).P("status", st.Status ?? "").P("detail", st.Detail).EndObj();
            }
            j.EndArr();
        }

        public string ReviewJson(ReceiverSettings s)
        {
            Json j = new Json(2048);
            j.Obj().P("ok", true);
            WriteSteps(j, Review(s));
            j.EndObj();
            return j.ToString();
        }

        public string ApplyJson(ReceiverSettings s)
        {
            bool busy;
            ReceiverReview r = Apply(s, out busy);
            Json j = new Json(2048);
            if (busy) { j.Obj().P("ok", false).P("error", "The setup is already running.").EndObj(); return j.ToString(); }
            bool ok = r.Blocked.Count == 0;
            for (int i = 0; i < r.Steps.Count; i++) if (r.Steps[i].Status == "failed") ok = false;
            j.Obj().P("ok", ok);
            WriteSteps(j, r);
            j.EndObj();
            return j.ToString();
        }

        /// <summary>The sign-in lines of "wevtutil qe OpenSSH/Operational /f:text", newest first as given.</summary>
        public static List<string> ParseSignIns(string text, int max)
        {
            string[] marks = { "Accepted password", "Failed password", "Invalid user" };
            List<string> found = new List<string>();
            string when = "";
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length && found.Count < max; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("Date:", StringComparison.Ordinal))
                {
                    DateTime t;
                    string v = line.Substring(5).Trim();
                    when = DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)
                        ? (t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : v;
                    continue;
                }
                for (int m = 0; m < marks.Length; m++)
                {
                    int at = line.IndexOf(marks[m], StringComparison.Ordinal);
                    if (at < 0) continue;
                    found.Add((when.Length > 0 ? when + "  " : "") + line.Substring(at));
                    break;
                }
            }
            return found;
        }

        /// <summary>The allowed addresses and port from "netsh advfirewall firewall show rule", or "" when the
        /// labels cannot be read (they are translated on a non-English Windows; the rule is then rewritten).</summary>
        public static string ParseFirewall(string text, out int port)
        {
            port = 0;
            Match p = Regex.Match(text ?? "", @"^\s*LocalPort:\s*(\d+)\s*$", RegexOptions.Multiline);
            if (p.Success) int.TryParse(p.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port);
            Match r = Regex.Match(text ?? "", @"^\s*RemoteIP:\s*(.+?)\s*$", RegexOptions.Multiline);
            if (!r.Success) return "";
            string[] a = r.Groups[1].Value.Split(',');
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = a[i].Trim();
                if (a[i].EndsWith("/32", StringComparison.Ordinal)) a[i] = a[i].Substring(0, a[i].Length - 3);
            }
            return string.Join(",", a);
        }
    }

    /// <summary>The real thing: Windows' own tools and calls.</summary>
    public sealed partial class WindowsReceiverOps : IReceiverOps
    {
        const string FirewallName = "CUBE CDR SFTP";

        static string Sys(string tool) { return Path.Combine(Environment.SystemDirectory, tool); }
        static string ConfigPath() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"ssh\sshd_config"); }

        static int Run(string exe, string args, int timeoutMs, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            StringBuilder sb = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler add = delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.OutputDataReceived += add; p.ErrorDataReceived += add;
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch (Exception) { }
                    output = "timed out";
                    return -1;
                }
                p.WaitForExit();
                lock (sb) output = sb.ToString().Trim();
                return p.ExitCode;
            }
        }

        public string SshdExe()
        {
            string[] c = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"OpenSSH\sshd.exe"),
                Sys(@"OpenSSH\sshd.exe") };
            for (int i = 0; i < c.Length; i++) if (File.Exists(c[i])) return c[i];
            return null;
        }

        public string Installer()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string[] where = { Path.Combine(dir, "openssh"), dir, Path.Combine(dir, @"..\openssh") };
            for (int i = 0; i < where.Length; i++)
            {
                if (!Directory.Exists(where[i])) continue;
                string[] f = Directory.GetFiles(where[i], "OpenSSH*.msi");
                if (f.Length == 0) continue;
                Array.Sort(f, StringComparer.OrdinalIgnoreCase);
                return Path.GetFullPath(f[f.Length - 1]);
            }
            return null;
        }

        public string Install(string msi)
        {
            string logs = Path.Combine(HostOptions.DefaultDataDir(), "logs");
            Directory.CreateDirectory(logs);
            string output;
            int rc = Run(Sys("msiexec.exe"), "/i \"" + msi + "\" ADDLOCAL=Server /qn /norestart /l*v \"" + Path.Combine(logs, "openssh-install.log") + "\"", 300000, out output);
            if (rc != 0 && rc != 3010) return "The OpenSSH installer stopped with code " + rc + ". Its log is " + Path.Combine(logs, "openssh-install.log");
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH");
            string path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
            if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
                Environment.SetEnvironmentVariable("Path", path.TrimEnd(';') + ";" + dir, EnvironmentVariableTarget.Machine);
            return null;
        }

        public string ServiceState()
        {
            try
            {
                using (ServiceController c = new ServiceController("sshd"))
                    return c.Status == ServiceControllerStatus.Running ? "Running" : "Stopped";
            }
            catch (InvalidOperationException) { return null; }
        }

        static string SetAuto()
        {
            string output;
            int rc = Run(Sys("sc.exe"), "config sshd start= auto", 15000, out output);
            return rc == 0 ? null : "OpenSSH could not be set to start with Windows: " + output;
        }

        public string StartAuto()
        {
            string e = SetAuto();
            if (e != null) return e;
            using (ServiceController c = new ServiceController("sshd"))
            {
                if (c.Status != ServiceControllerStatus.Running)
                {
                    c.Start();
                    c.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            return null;
        }

        public string Restart()
        {
            string e = SetAuto();
            if (e != null) return e;
            using (ServiceController c = new ServiceController("sshd"))
            {
                if (c.Status != ServiceControllerStatus.Stopped)
                {
                    c.Stop();
                    c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                c.Start();
                c.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            return null;
        }

        public string ConfigText()
        {
            try
            {
                byte[] b = File.ReadAllBytes(ConfigPath());
                int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
                return Encoding.UTF8.GetString(b, skip, b.Length - skip);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public string WriteConfig(string text, out string backup)
        {
            string path = ConfigPath();
            backup = path + ".before-cubescope-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Copy(path, backup, true);
            // over the same file, so its permissions stay; OpenSSH cannot read a file that starts with a byte-order mark
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
            return null;
        }

        public string RestoreConfig(string backup)
        {
            File.WriteAllBytes(ConfigPath(), File.ReadAllBytes(backup));
            return null;
        }

        public string TestConfig(string sshd)
        {
            string output;
            int rc = Run(sshd, "-t", 30000, out output);
            return rc == 0 ? null : (output.Length > 0 ? output : "exit code " + rc);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct USER_INFO_1
        {
            public string name, password;
            public int password_age, priv;
            public string home_dir, comment;
            public int flags;
            public string script_path;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct USER_INFO_1003 { public string password; }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetUserAdd(string server, int level, ref USER_INFO_1 buf, out int parmErr);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetUserSetInfo(string server, string user, int level, ref USER_INFO_1003 buf, out int parmErr);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetUserGetInfo(string server, string user, int level, out IntPtr buf);
        [DllImport("netapi32.dll")]
        static extern int NetApiBufferFree(IntPtr buf);

        static string NetError(int rc)
        {
            if (rc == 2245) return "Windows refused this password: it does not meet this server's password rules. Try a longer one with letters, numbers and symbols.";
            if (rc == 2224) return "That account already exists.";
            if (rc == 2221) return "That account does not exist.";
            if (rc == 5) return "Windows refused: this needs an administrator window.";
            return "Windows refused (error " + rc + "): " + new Win32Exception(rc).Message;
        }

        public int AccountKind(string name)
        {
            IntPtr buf;
            int rc = NetUserGetInfo(null, name, 1, out buf);
            if (rc != 0) return 0;
            try
            {
                USER_INFO_1 u = (USER_INFO_1)Marshal.PtrToStructure(buf, typeof(USER_INFO_1));
                return u.priv == 2 ? 2 : 1;
            }
            finally { NetApiBufferFree(buf); }
        }

        public string CreateAccount(string name, string password)
        {
            USER_INFO_1 u = new USER_INFO_1();
            u.name = name; u.password = password; u.priv = 1;
            u.comment = "CUBE call records over SFTP (set up by CubeScope)";
            u.flags = 0x0001 | 0x0200 | 0x10000;    // logon script, normal account, password never expires
            int err;
            int rc = NetUserAdd(null, 1, ref u, out err);
            return rc == 0 ? null : NetError(rc);
        }

        public string SetPassword(string name, string password)
        {
            USER_INFO_1003 u = new USER_INFO_1003();
            u.password = password;
            int err;
            int rc = NetUserSetInfo(null, name, 1003, ref u, out err);
            return rc == 0 ? null : NetError(rc);
        }

        public bool FolderExists(string path) { return Directory.Exists(path); }

        public bool FolderAllowed(string path, string account)
        {
            try
            {
                string suffix = "\\" + account;
                DirectorySecurity ds = Directory.GetAccessControl(path);
                foreach (FileSystemAccessRule r in ds.GetAccessRules(true, true, typeof(NTAccount)))
                {
                    if (r.AccessControlType != AccessControlType.Allow) continue;
                    if (!r.IdentityReference.Value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((r.FileSystemRights & FileSystemRights.Modify) != FileSystemRights.Modify) continue;
                    if ((r.InheritanceFlags & InheritanceFlags.ObjectInherit) == 0 || (r.InheritanceFlags & InheritanceFlags.ContainerInherit) == 0) continue;
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        public string AllowFolder(string path, string account)
        {
            Directory.CreateDirectory(path);
            DirectorySecurity ds = Directory.GetAccessControl(path);
            // this computer's account, not one of the same name in a domain
            ds.AddAccessRule(new FileSystemAccessRule(new NTAccount(Environment.MachineName, account),
                FileSystemRights.Modify | FileSystemRights.Synchronize,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, ds);
            return null;
        }

        public string PortBanner(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient(AddressFamily.InterNetwork))
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return null;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = 1500;
                    byte[] b = new byte[256];
                    int n;
                    try { n = c.GetStream().Read(b, 0, b.Length); }
                    catch (IOException) { return ""; }
                    string text = Encoding.ASCII.GetString(b, 0, n);
                    int nl = text.IndexOf('\n');
                    return (nl >= 0 ? text.Substring(0, nl) : text).Trim();
                }
            }
            catch (SocketException) { return null; }
        }

        public string FirewallRule(out int port)
        {
            port = 0;
            string output;
            int rc = Run(Sys("netsh.exe"), "advfirewall firewall show rule name=\"" + FirewallName + "\"", 15000, out output);
            return rc == 0 ? ReceiverAdmin.ParseFirewall(output, out port) : null;
        }

        public string SetFirewall(int port, string[] allowed)
        {
            string output;
            Run(Sys("netsh.exe"), "advfirewall firewall delete rule name=\"" + FirewallName + "\"", 15000, out output);
            int rc = Run(Sys("netsh.exe"), "advfirewall firewall add rule name=\"" + FirewallName + "\" dir=in action=allow protocol=TCP localport=" +
                         port.ToString(CultureInfo.InvariantCulture) + " remoteip=" + string.Join(",", allowed) + " profile=any", 15000, out output);
            return rc == 0 ? null : "Windows Firewall refused the rule: " + output;
        }

        public List<ReceiverFile> NewestFiles(string folder, int max)
        {
            List<ReceiverFile> list = new List<ReceiverFile>();
            try
            {
                FileInfo[] f = new DirectoryInfo(folder).GetFiles();
                Array.Sort(f, delegate (FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                for (int i = 0; i < f.Length && i < max; i++)
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

        public List<string> RecentSignIns(int max)
        {
            try
            {
                string output;
                int rc = Run(Sys("wevtutil.exe"), "qe OpenSSH/Operational /c:60 /rd:true /f:text", 15000, out output);
                if (rc == 0) return ReceiverAdmin.ParseSignIns(output, max);
            }
            catch (Exception) { }
            return new List<string>();
        }

        public List<string> LocalAddresses()
        {
            List<string> list = new List<string>();
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in n.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string s = a.Address.ToString();
                    if (!s.StartsWith("169.254.", StringComparison.Ordinal) && !list.Contains(s)) list.Add(s);
                }
            }
            return list;
        }

        public void Wait(int ms) { Thread.Sleep(ms); }

        public string ExePath() { return System.Reflection.Assembly.GetExecutingAssembly().Location; }

        public string CubeScopeServiceFolder()
        {
            string output;
            int rc = Run(Sys("sc.exe"), "qc " + CubeScopeService.Name + " 4096", 15000, out output);
            if (rc != 0) return null;
            Match m = Regex.Match(output, @"--logs\s+""([^""]+)""");
            return m.Success ? m.Groups[1].Value : "";
        }

        public string InstallCubeScopeService(string folder)
        {
            HostOptions h = new HostOptions();
            h.Load.Root = folder; h.Load.Hours = 24; h.LocalOnly = true; h.Alarms = true;
            // Install reports to the console; here the page needs its words
            TextWriter o = Console.Out, e = Console.Error;
            StringWriter said = new StringWriter();
            int rc;
            Console.SetOut(said); Console.SetError(said);
            try { rc = CubeScopeService.Install(h, null); }
            finally { Console.SetOut(o); Console.SetError(e); }
            Log.Info("admin: service install said: " + said.ToString().Trim());
            return rc == 0 ? null : "The CubeScope service could not be set up: " + said.ToString().Trim();
        }
    }

    /// <summary>A pretend server for the self-test and --admin-simulate. Changes nothing on this computer.</summary>
    public sealed partial class SimulatedReceiverOps : IReceiverOps
    {
        public const string SampleConfig =
            "# This is the sshd server system-wide configuration file.\n" +
            "#Port 22\n#AddressFamily any\n#ListenAddress 0.0.0.0\n\n" +
            "AuthorizedKeysFile\t.ssh/authorized_keys\n\n" +
            "# override default of no subsystems\nSubsystem\tsftp\tsftp-server.exe\n\n" +
            "Match Group administrators\n       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys\n";

        public string Exe, Msi = @"C:\CubeScope\openssh\OpenSSH-Win64-test.msi", Service, Config, FwRemote, FailTest, Backup;
        public int Kind, FwPort, OtherPort;
        public string OtherBanner = "";
        public bool Folder, Granted;
        public readonly List<string> Calls = new List<string>();

        public string SshdExe() { return Exe; }
        public string Installer() { return Msi; }
        public string Install(string msi) { Calls.Add("install"); Exe = @"C:\Program Files\OpenSSH\sshd.exe"; Service = "Stopped"; return null; }
        public string ServiceState() { return Service; }
        public string StartAuto() { Calls.Add("start"); Service = "Running"; if (Config == null) Config = SampleConfig; return null; }
        public string Restart() { Calls.Add("restart"); Service = "Running"; return null; }
        public string ConfigText() { return Config; }
        public string WriteConfig(string text, out string backup) { Calls.Add("write"); backup = "backup"; Backup = Config; Config = text; return null; }
        public string RestoreConfig(string backup) { Calls.Add("restore"); Config = Backup; return null; }
        public string TestConfig(string sshd) { Calls.Add("test"); return FailTest; }
        public int AccountKind(string name) { return Kind; }
        public string CreateAccount(string name, string password) { Calls.Add("create"); Kind = 1; return null; }
        public string SetPassword(string name, string password) { Calls.Add("password"); return null; }
        public bool FolderExists(string path) { return Folder; }
        public bool FolderAllowed(string path, string account) { return Granted; }
        public string AllowFolder(string path, string account) { Calls.Add("folder"); Folder = true; Granted = true; return null; }

        public string PortBanner(int port)
        {
            if (OtherPort == port) return OtherBanner;
            if (Service != "Running" || Config == null) return null;
            int p = SshdConfig.ActivePort(Config);
            return (p == 0 ? 22 : p) == port ? "SSH-2.0-OpenSSH_for_Windows_simulated" : null;
        }

        public string FirewallRule(out int port) { port = FwPort; return FwRemote; }
        public string SetFirewall(int port, string[] allowed) { Calls.Add("firewall"); FwPort = port; FwRemote = string.Join(",", allowed); return null; }

        public List<ReceiverFile> NewestFiles(string folder, int max)
        {
            List<ReceiverFile> l = new List<ReceiverFile>();
            if (FwRemote != null)
            {
                ReceiverFile f = new ReceiverFile();
                f.Name = "cdr.example-cube.11_09_2026_18_00_00.000"; f.Size = 48213; f.When = "2026-11-09 13:05:00";
                l.Add(f);
            }
            return l;
        }

        public List<string> RecentSignIns(int max)
        {
            List<string> l = new List<string>();
            if (FwRemote != null) l.Add("2026-11-09 13:05:00  Accepted password for cube-cdr from 192.0.2.10 port 52001 ssh2");
            return l;
        }

        public List<string> LocalAddresses() { return new List<string> { "192.0.2.50", "198.51.100.50" }; }
        public void Wait(int ms) { }

        public string ServiceFolder;
        public string CubeScopeServiceFolder() { return ServiceFolder; }
        public string InstallCubeScopeService(string folder) { Calls.Add("service"); ServiceFolder = folder; return null; }
        public string ExePath() { return @"C:\CubeScope\CubeScope.exe"; }
    }
}
