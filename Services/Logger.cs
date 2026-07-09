using System;
using System.IO;

namespace Wisp.Services
{
    public static class Logger
    {
        private static readonly string LogFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wisp");
        private static readonly string LogPath = Path.Combine(LogFolder, "wisp.log");
        private static readonly object LockObj = new object();

        static Logger()
        {
            try
            {
                if (!Directory.Exists(LogFolder))
                {
                    Directory.CreateDirectory(LogFolder);
                }

                var fileInfo = new FileInfo(LogPath);
                if (fileInfo.Exists && fileInfo.Length > 2 * 1024 * 1024)
                {
                    File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] Log file rotated due to size.\n");
                }
            }
            catch { }
        }

        public static void Info(string message) => Log("INFO", message);
        public static void Warn(string message) => Log("WARN", message);
        public static void Error(string message, Exception? ex = null)
        {
            string msg = message;
            if (ex != null)
            {
                msg += $"\nException: {ex.Message}\nStackTrace: {ex.StackTrace}";
            }
            Log("ERROR", msg);
        }

        private static void Log(string level, string message)
        {
            lock (LockObj)
            {
                try
                {
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n";
                    File.AppendAllText(LogPath, logLine);
                    System.Diagnostics.Debug.Write(logLine);
                }
                catch { }
            }
        }
    }
}
