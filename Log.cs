using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// The troubleshooting log of a long-running CubeScope (the service, or --serve with --alarms):
    /// timestamps, version, settings, load and rebuild timings, every alarm judgement and delivery,
    /// and errors with their stack traces. Three files of 2 MB at most (cubescope.log, .1, .2), so it
    /// cannot grow for ever. Writing never throws: a log that cannot be written is skipped.
    /// </summary>
    public static class Log
    {
        public const long MaxBytes = 2L * 1024 * 1024;
        public const int Keep = 3;
        static readonly object Sync = new object();
        static string _file;
        public static bool Echo = true;          // also to the console (off in the service)

        public static string FilePath { get { return _file; } }

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _file = Path.Combine(dir, "cubescope.log");
            }
            catch (Exception) { _file = null; }
        }

        public static void Info(string text) { Write("INFO ", text); }
        public static void Warn(string text) { Write("WARN ", text); }
        public static void Error(string text, Exception ex)
        {
            Write("ERROR", text + (ex != null ? Environment.NewLine + ex : ""));
        }

        static void Write(string level, string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + level + "  " + text;
            if (Echo) { try { Console.Error.WriteLine(line); } catch (Exception) { } }
            if (_file == null) return;
            lock (Sync)
            {
                try
                {
                    FileInfo fi = new FileInfo(_file);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        for (int i = Keep - 1; i >= 1; i--)
                        {
                            string from = i == 1 ? _file : _file + "." + (i - 1);
                            string to = _file + "." + i;
                            if (File.Exists(to)) File.Delete(to);
                            if (File.Exists(from)) File.Move(from, to);
                        }
                    }
                    File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception) { }
            }
        }

        /// <summary>The last lines of the current log file (for the page's "View log" link).</summary>
        public static string Tail(int lines)
        {
            if (_file == null) return "(no log file)";
            lock (Sync)
            {
                try
                {
                    using (FileStream fs = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long start = Math.Max(0, fs.Length - 256 * 1024);
                        fs.Seek(start, SeekOrigin.Begin);
                        string text = new StreamReader(fs, Encoding.UTF8).ReadToEnd();
                        string[] all = text.Split('\n');
                        int from = Math.Max(start > 0 ? 1 : 0, all.Length - lines);
                        StringBuilder sb = new StringBuilder();
                        for (int i = from; i < all.Length; i++) sb.Append(all[i].TrimEnd('\r')).Append('\n');
                        return sb.ToString();
                    }
                }
                catch (Exception ex) { return "(cannot read " + _file + ": " + ex.Message + ")"; }
            }
        }
    }
}
