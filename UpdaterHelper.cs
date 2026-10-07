using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    public static class UpdaterHelper
    {
        private delegate bool WindowCallback(IntPtr handle, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
        private static bool HasVisibleWindow(int processId)
        {
            bool found = false;
            EnumWindows((handle, data) => { uint owner; GetWindowThreadProcessId(handle, out owner);
                if (owner == processId && IsWindowVisible(handle)) {
                    var title = new StringBuilder(128); GetWindowText(handle, title, title.Capacity);
                    if (title.ToString().StartsWith("캘린더 위젯 ·", StringComparison.Ordinal)) found = true;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
        private static void Report(string root, bool updated, bool rolledBack, string kind)
        {
            try {
                var path = Path.Combine(root, ".updates", "last-result.json");
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(new {
                    updated, rolledBack, failureKind = kind, atUtc = DateTimeOffset.UtcNow.ToString("o") }));
            } catch { }
        }
        private static Process Launch(string root, bool oauth)
        {
            return Process.Start(new ProcessStartInfo(Path.Combine(root, "CalendarWidget.exe"), oauth ? "--enable-oauth --startup" : "--startup") {
                WorkingDirectory = root, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        private static bool Visible(Process process)
        {
            var deadline = DateTime.UtcNow.AddSeconds(18);
            while (DateTime.UtcNow < deadline) {
                Thread.Sleep(250);
                process.Refresh();
                if (process.HasExited) return false;
                if (HasVisibleWindow(process.Id) && process.Responding) return true;
            }
            return false;
        }
        public static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            UpdateRequest request = null; string root = null, backup = null;
            try {
                request = new JavaScriptSerializer().Deserialize<UpdateRequest>(File.ReadAllText(args[0]));
                File.Delete(args[0]);
                if (request == null || request.OldProcessId <= 0 || !UpdateCore.Valid(request.Release)) return 2;
                root = Path.GetFullPath(request.Root);
                string stage = Path.GetFullPath(Path.Combine(root, ".updates", "staged")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(request.PackagePath).StartsWith(stage, StringComparison.OrdinalIgnoreCase)) return 2;
                try {
                    var old = Process.GetProcessById(request.OldProcessId);
                    if (old.ProcessName != "CalendarWidget" || !old.WaitForExit(30000)) { Report(root, false, false, "old-app-still-running"); return 3; }
                } catch (ArgumentException) { }
                backup = UpdateCore.Apply(root, request.PackagePath, request.Release);
                var next = Launch(root, request.EnableOAuth);
                if (next == null || !Visible(next)) throw new InvalidOperationException("new-app-did-not-start");
                Report(root, true, false, null);
                return 0;
            } catch (Exception error) {
                bool restored = false;
                if (root != null && backup != null) {
                    try {
                        foreach (var process in Process.GetProcessesByName("CalendarWidget")) {
                            try { if (String.Equals(process.MainModule.FileName, Path.Combine(root, "CalendarWidget.exe"), StringComparison.OrdinalIgnoreCase)) {
                                process.Kill(); process.WaitForExit(5000);
                            } } catch { }
                        }
                        UpdateCore.Restore(root, backup); restored = true;
                    } catch { }
                }
                if (root != null) {
                    Report(root, false, restored, error is InvalidDataException ? "invalid-package" : error is IOException ? "file-operation" : "launch-failed");
                    try { Launch(root, request != null && request.EnableOAuth); } catch { }
                }
                return 1;
            }
        }
    }
}
