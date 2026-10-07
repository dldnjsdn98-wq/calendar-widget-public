using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    public sealed class UpdateRelease
    {
        public string Application { get; set; }
        public string Version { get; set; }
        public string Package { get; set; }
        public string Sha256 { get; set; }
    }
    public sealed class UpdateRequest
    {
        public string Root { get; set; }
        public string PackagePath { get; set; }
        public UpdateRelease Release { get; set; }
        public int OldProcessId { get; set; }
        public bool EnableOAuth { get; set; }
    }
    public static class UpdateCore
    {
        public const string DefaultFeed = "https://github.com/dldnjsdn98-wq/calendar-widget-public/releases/latest/download/release.json";
        public static readonly string[] RuntimeFiles = { "CalendarWidget.exe", "CalendarWidget.exe.config", "CalendarWidgetUpdater.exe",
            "widget.xaml", "settings.xaml", "day-popup.xaml", "manual-event.xaml", "preview.xaml", "sample-data.json" };
        private static readonly HashSet<string> AllowedFiles = new HashSet<string>(RuntimeFiles, StringComparer.OrdinalIgnoreCase);
        public static Version CurrentVersion { get { return typeof(UpdateCore).Assembly.GetName().Version; } }
        public static string VersionText { get { return CurrentVersion.ToString(3); } }
        public static bool Valid(UpdateRelease release)
        {
            Version parsed;
            return release != null && release.Application == "CalendarWidget" &&
                Regex.IsMatch(release.Version ?? "", @"^\d+\.\d+\.\d+$") && Version.TryParse(release.Version, out parsed) && parsed.Major >= 1 && parsed.Build >= 0 &&
                release.Package == "CalendarWidget-v" + release.Version + ".zip" &&
                Regex.IsMatch(release.Sha256 ?? "", "^[0-9a-fA-F]{64}$");
        }
        public static bool IsNewer(UpdateRelease release)
        {
            Version parsed;
            return Valid(release) && Version.TryParse(release.Version, out parsed) && parsed > CurrentVersion;
        }
        public static string HashFile(string path)
        {
            using (var input = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        public static bool MatchesHash(string path, string expected)
        {
            return File.Exists(path) && String.Equals(HashFile(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        private static string SafeFeedFile(string feed, string file)
        {
            var directory = Path.GetFullPath(feed);
            if (Path.GetFileName(file) != file) throw new InvalidDataException("Invalid release file name.");
            return Path.Combine(directory, file);
        }
        private static Uri ManifestUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
            if (!uri.AbsolutePath.EndsWith("/release.json", StringComparison.OrdinalIgnoreCase) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("HTTPS release address must be a plain release.json URL.");
            return uri;
        }
        private static async Task<byte[]> GetSmallHttps(string url, CancellationToken cancellation)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)) {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage.RequestUri.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength > 65536)
                    throw new InvalidDataException("Invalid release response.");
                using (var input = await response.Content.ReadAsStreamAsync()) using (var output = new MemoryStream()) {
                    var buffer = new byte[8192]; int n;
                    while ((n = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)) > 0) {
                        if (output.Length + n > 65536) throw new InvalidDataException("Release manifest too large.");
                        output.Write(buffer, 0, n);
                    }
                    return output.ToArray();
                }
            }
        }
        public static async Task<UpdateRelease> Check(string feed, CancellationToken cancellation)
        {
            if (String.IsNullOrWhiteSpace(feed)) return null;
            byte[] raw;
            var manifestUri = ManifestUri(feed);
            if (manifestUri != null) raw = await GetSmallHttps(manifestUri.AbsoluteUri, cancellation);
            else {
                Uri other;
                if (Uri.TryCreate(feed, UriKind.Absolute, out other) && other.Scheme != Uri.UriSchemeFile)
                    throw new InvalidDataException("Only local folders or HTTPS release manifests are supported.");
                var path = SafeFeedFile(feed, "release.json");
                if (!File.Exists(path)) return null;
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Release manifest too large.");
                raw = await Task.Run(() => File.ReadAllBytes(path), cancellation);
            }
            var release = new JavaScriptSerializer().Deserialize<UpdateRelease>(System.Text.Encoding.UTF8.GetString(raw));
            if (!Valid(release)) throw new InvalidDataException("Invalid release manifest.");
            return release;
        }
        public static async Task<string> Prepare(string root, string feed, UpdateRelease release, CancellationToken cancellation)
        {
            if (!Valid(release)) throw new InvalidDataException("Invalid release manifest.");
            string folder = Path.Combine(root, ".updates", "staged"); Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, release.Package);
            string temporary = destination + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            try {
                var manifestUri = ManifestUri(feed);
                if (manifestUri != null) {
                    var uri = new Uri(manifestUri, release.Package);
                    using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                    using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation)) {
                        response.EnsureSuccessStatusCode();
                        if (response.RequestMessage.RequestUri.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentLength > 52428800)
                            throw new InvalidDataException("Invalid release package response.");
                        using (var input = await response.Content.ReadAsStreamAsync()) using (var output = File.Create(temporary)) {
                            var buffer = new byte[65536]; int n;
                            while ((n = await input.ReadAsync(buffer, 0, buffer.Length, cancellation)) > 0) {
                                if (output.Length + n > 52428800) throw new InvalidDataException("Release package too large.");
                                await output.WriteAsync(buffer, 0, n, cancellation);
                            }
                        }
                    }
                } else await Task.Run(() => {
                    var source = SafeFeedFile(feed, release.Package);
                    if (new FileInfo(source).Length > 52428800) throw new InvalidDataException("Release package too large.");
                    File.Copy(source, temporary, true);
                }, cancellation);
                if (!MatchesHash(temporary, release.Sha256)) throw new InvalidDataException("Release package hash mismatch.");
                if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
                return destination;
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static string Apply(string root, string package, UpdateRelease release, int injectFailureAfter = -1)
        {
            if (!Valid(release) || !MatchesHash(package, release.Sha256)) throw new InvalidDataException("Release package hash mismatch.");
            root = Path.GetFullPath(root);
            var backup = Path.Combine(root, ".updates", "backups", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            var unpacked = Path.Combine(root, ".updates", "unpacked", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup); Directory.CreateDirectory(unpacked);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool backupComplete = false;
            try {
                using (var archive = ZipFile.OpenRead(package)) {
                    long size = 0;
                    foreach (var item in archive.Entries) {
                        if (!AllowedFiles.Contains(item.FullName) || !names.Add(item.FullName) || item.Length > 52428800 || (size += item.Length) > 52428800)
                            throw new InvalidDataException("Release contains an unsupported file.");
                        using (var source = item.Open()) using (var target = File.Create(Path.Combine(unpacked, item.FullName))) source.CopyTo(target);
                    }
                }
                if (names.Count != AllowedFiles.Count) throw new InvalidDataException("Release is missing required files.");
                var fileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(unpacked, "CalendarWidget.exe")).FileVersion;
                if (fileVersion == null || !fileVersion.StartsWith(release.Version + ".", StringComparison.Ordinal))
                    throw new InvalidDataException("Release version does not match the executable.");
                foreach (var name in RuntimeFiles) {
                    var installed = Path.Combine(root, name);
                    if (File.Exists(installed)) File.Copy(installed, Path.Combine(backup, name), true);
                }
                backupComplete = true;
                int replaced = 0;
                foreach (var name in RuntimeFiles) {
                    var installed = Path.Combine(root, name);
                    var next = installed + ".update-next";
                    File.Copy(Path.Combine(unpacked, name), next, true);
                    if (File.Exists(installed)) File.Replace(next, installed, null); else File.Move(next, installed);
                    if (++replaced == injectFailureAfter) throw new IOException("Injected update failure.");
                }
                File.WriteAllText(Path.Combine(backup, "version.txt"), release.Version);
                return backup;
            } catch {
                if (backupComplete) Restore(root, backup, names);
                throw;
            } finally { if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true); }
        }
        public static void Restore(string root, string backup, IEnumerable<string> packageNames = null)
        {
            foreach (var name in packageNames ?? RuntimeFiles) {
                if (!AllowedFiles.Contains(name)) continue;
                string old = Path.Combine(backup, name), installed = Path.Combine(root, name);
                if (File.Exists(old)) File.Copy(old, installed, true);
                else if (File.Exists(installed)) File.Delete(installed);
            }
        }
    }
}
