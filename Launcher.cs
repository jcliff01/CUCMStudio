using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CubeScope
{
    /// <summary>
    /// Double-click start. When CubeScope.exe is opened from Explorer with no arguments it asks for
    /// the syslog folder with the Windows folder browser, checks that it (or a folder under it) holds
    /// .log files, and serves the dashboard on a free port with the browser opened on it. Run from a
    /// command prompt with no arguments it still prints the usage text.
    ///
    /// The folder browser is the shell's IFileOpenDialog in pick-folders mode, called through COM
    /// interop, so it needs nothing beyond what csc already references - no Windows Forms.
    /// (Copied from DialerScope's Launcher; keep the COM vtable order identical.)
    /// </summary>
    public static class Launcher
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint GetConsoleProcessList(uint[] processList, uint count);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        /// <summary>
        /// True when Explorer (or a shortcut) started this exe: the console window was created for
        /// it alone, so nothing else is attached to it. From cmd, PowerShell or Windows Terminal the
        /// shell shares the console and the count is two or more. Redirected output means a script
        /// or tool is driving it, which must never get a dialog.
        /// </summary>
        public static bool LaunchedByDoubleClick()
        {
            try
            {
                if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;
                uint[] list = new uint[8];
                return GetConsoleProcessList(list, (uint)list.Length) == 1;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Asks for the folder and returns the arguments to run with, or null when the user cancels.
        /// Loops until the pick holds syslog .log files or the user gives up.
        /// </summary>
        public static string[] AskForArgs()
        {
            Console.WriteLine("CubeScope " + Program.Version);
            Console.WriteLine();
            Console.WriteLine("Choose the folder the syslog server writes the CUBE's files to, for example");
            Console.WriteLine(@"D:\logs\10.1.1.5 (one CUBE) or D:\logs (every CUBE in its own folder).");
            Console.WriteLine();

            string start = LastFolder();
            bool admin = CubeScopeService.IsAdmin();
            if (admin) { Console.WriteLine("Cancel the folder window to open the Admin page instead."); Console.WriteLine(); }
            while (true)
            {
                string picked = PickFolder("CubeScope - choose the CUBE's folder (syslog .log or cdr files)" + (admin ? ", or cancel for the Admin page" : ""), start);
                if (picked == null) return null;
                Console.WriteLine("  picked " + picked);
                FolderResolution r = Resolve(picked, 2);
                if (r.Matches.Count > 0)
                {
                    SaveLastFolder(picked);
                    int port = FreePort(8792, 20, ListeningPorts());
                    Console.WriteLine("  using   " + picked + " (" + r.Message + ")");
                    Console.WriteLine();
                    Console.WriteLine("Starting. Keep this window open while you use the dashboard; close it to stop.");
                    Console.WriteLine();
                    return new string[] { "--logs", picked, "--serve", port.ToString(), "--open" };
                }
                Console.WriteLine();
                Console.WriteLine("  " + r.Message);
                Console.WriteLine("  Pick again, or cancel the folder window to exit.");
                Console.WriteLine();
                start = picked;
            }
        }

        // ------------------------------------------------------------ finding the logs

        public sealed class FolderResolution
        {
            /// <summary>Folders holding .log files: the picked one and up to two levels under it.</summary>
            public readonly List<string> Matches = new List<string>();
            /// <summary>What was found, or why nothing was, in words for the console.</summary>
            public string Message = "";
            public int Files;
        }

        /// <summary>The picked folder and the folders under it (to maxDepth) that hold .log files.</summary>
        public static FolderResolution Resolve(string picked, int maxDepth)
        {
            FolderResolution r = new FolderResolution();
            if (string.IsNullOrEmpty(picked) || !Directory.Exists(picked))
            {
                r.Message = "That folder cannot be opened: " + picked;
                return r;
            }
            Walk(picked, 0, maxDepth, r);
            if (r.Matches.Count == 0)
                r.Message = "No .log files in that folder or the folders under it (" + maxDepth + " levels).";
            else
                r.Message = r.Files + " log file" + (r.Files == 1 ? "" : "s") + " in " + r.Matches.Count + " folder" +
                            (r.Matches.Count == 1 ? "" : "s");
            return r;
        }

        static void Walk(string dir, int depth, int maxDepth, FolderResolution r)
        {
            int n = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.log")) { n++; if (n > 100000) break; }
                foreach (string f in Directory.EnumerateFiles(dir, Loader.CdrPattern))
                    if (Loader.IsCdrName(Path.GetFileName(f))) { n++; if (n > 100000) break; }
            }
            catch (Exception) { }
            if (n > 0) { r.Matches.Add(dir); r.Files += n; }
            if (depth >= maxDepth) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < subs.Length; i++)
            {
                try { if ((File.GetAttributes(subs[i]) & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception) { continue; }
                Walk(subs[i], depth + 1, maxDepth, r);
            }
        }

        // ------------------------------------------------------------ port and memory

        /// <summary>The first port from start that nothing is listening on; start if all are taken.</summary>
        public static int FreePort(int start, int tries, ICollection<int> listening)
        {
            for (int p = start; p < start + tries; p++)
                if (!listening.Contains(p)) return p;
            return start;
        }

        public static HashSet<int> ListeningPorts()
        {
            HashSet<int> s = new HashSet<int>();
            try
            {
                foreach (System.Net.IPEndPoint ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                    s.Add(ep.Port);
            }
            catch (Exception) { }
            return s;
        }

        static string SettingsFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "CubeScope" + Path.DirectorySeparatorChar + "last-logs-folder.txt");
        }

        static string LastFolder()
        {
            try
            {
                string f = SettingsFile();
                if (!File.Exists(f)) return null;
                string s = File.ReadAllText(f, Encoding.UTF8).Trim();
                return s.Length > 0 ? s : null;
            }
            catch (Exception) { return null; }
        }

        static void SaveLastFolder(string folder)
        {
            try
            {
                string f = SettingsFile();
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllText(f, folder, new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------ the shell folder browser

        const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40,
                   FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;
        const int ERROR_CANCELLED_HR = unchecked((int)0x800704C7);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCo { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);
            // IFileDialog, in vtable order - do not reorder
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            [PreserveSig] int GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

        static IShellItem ItemFor(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return null;
            try
            {
                Guid iid = typeof(IShellItem).GUID;
                IShellItem item;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
                return item;
            }
            catch (Exception) { return null; }
        }

        static IFileDialog NewDialog(string title, string startFolder)
        {
            IFileDialog d = (IFileDialog)new FileOpenDialogCo();
            uint fos;
            d.GetOptions(out fos);
            d.SetOptions(fos | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
            d.SetTitle(title);
            d.SetOkButtonLabel("Use this folder");
            IShellItem start = ItemFor(startFolder);
            if (start != null) d.SetFolder(start);
            return d;
        }

        /// <summary>
        /// Shows the folder browser in front of the console window and returns the chosen path, or
        /// null on cancel. The shell dialog needs a single-threaded apartment, so it runs on one.
        /// </summary>
        public static string PickFolder(string title, string startFolder)
        {
            string result = null;
            Exception failure = null;
            Thread t = new Thread(delegate ()
            {
                try
                {
                    IFileDialog d = NewDialog(title, startFolder);
                    int hr = d.Show(GetConsoleWindow());
                    if (hr == ERROR_CANCELLED_HR) return;
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                    IShellItem item;
                    hr = d.GetResult(out item);
                    if (hr < 0 || item == null) return;
                    string path;
                    item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    result = path;
                }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (failure != null) throw new InvalidOperationException("the folder browser failed: " + failure.Message, failure);
            return result;
        }

        /// <summary>
        /// Exercises the dialog's COM plumbing WITHOUT showing it (nothing appears on screen):
        /// creates it, round-trips options, a title-adjacent string slot and a start folder, and
        /// checks that asking for a result before Show fails the way the shell documents. A wrong
        /// vtable order in IFileDialog shows up here as a crash or a value that does not round-trip.
        /// Returns a list of problems; empty means the plumbing is sound.
        /// </summary>
        public static List<string> CheckDialogPlumbing(string folder)
        {
            List<string> problems = new List<string>();
            Thread t = new Thread(delegate ()
            {
                try
                {
                    IFileDialog d = NewDialog("CubeScope plumbing check", folder);
                    uint fos;
                    d.GetOptions(out fos);
                    if ((fos & FOS_PICKFOLDERS) == 0) problems.Add("pick-folders option did not round-trip (" + fos.ToString("x") + ")");
                    d.SetFileName("cubescope-check");
                    string back;
                    d.GetFileName(out back);
                    if (back != "cubescope-check") problems.Add("file name slot did not round-trip: " + back);
                    IShellItem cur;
                    d.GetFolder(out cur);
                    string path = null;
                    if (cur != null) cur.GetDisplayName(SIGDN_FILESYSPATH, out path);
                    if (!string.Equals((path ?? "").TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        problems.Add("start folder did not round-trip: " + path);
                    IShellItem none;
                    int hr = d.GetResult(out none);
                    if (hr >= 0) problems.Add("GetResult before Show unexpectedly succeeded");
                    Marshal.ReleaseComObject(d);
                }
                catch (Exception ex) { problems.Add("COM call failed: " + ex.GetType().Name + ": " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return problems;
        }
    }
}
