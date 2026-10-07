using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CalendarWidget
{
    public static class UpdateVerification
    {
        private static string Folder(string root, string name)
        {
            var result = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(result); return result;
        }
        public static void Run(string appRoot, string output, Action<string, bool> check)
        {
            string feed = Folder(output, "update-feed");
            var release = new UpdateRelease { Application = "CalendarWidget", Version = UpdateCore.VersionText,
                Package = "CalendarWidget-v" + UpdateCore.VersionText + ".zip" };
            var zip = Path.Combine(feed, release.Package);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var name in UpdateCore.RuntimeFiles) archive.CreateEntryFromFile(Path.Combine(appRoot, name), name);
            release.Sha256 = UpdateCore.HashFile(zip);
            File.WriteAllText(Path.Combine(feed, "release.json"), JsonData.Serialize(release));
            var observed = Task.Run(() => UpdateCore.Check(feed, CancellationToken.None)).Result;
            check("update_release_version_is_separate_from_git_commit", observed.Version == UpdateCore.VersionText && !UpdateCore.IsNewer(observed));
            check("update_manifest_rejects_traversal_and_untrusted_hash", !UpdateCore.Valid(new UpdateRelease {
                Application = "CalendarWidget", Version = "1.1.1", Package = "../evil.zip", Sha256 = release.Sha256 }) &&
                !UpdateCore.Valid(new UpdateRelease { Application = "CalendarWidget", Version = "1.1.1", Package = "CalendarWidget-v1.1.1.zip", Sha256 = "x" }));
            bool rejectedTokenUrl = false;
            try { UpdateCore.Check("https://example.invalid/releases/latest/download/release.json?token=synthetic", CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { rejectedTokenUrl = true; }
            check("update_feed_rejects_embedded_token_url", rejectedTokenUrl);
            check("update_detects_later_release", UpdateCore.IsNewer(new UpdateRelease { Application = "CalendarWidget",
                Version = "1.1.3", Package = "CalendarWidget-v1.1.3.zip", Sha256 = release.Sha256 }));
            string installed = Folder(output, "update-install");
            foreach (var name in UpdateCore.RuntimeFiles) File.WriteAllText(Path.Combine(installed, name), "old:" + name);
            File.WriteAllText(Path.Combine(installed, "settings.json"), "private-settings-sentinel");
            Directory.CreateDirectory(Path.Combine(installed, ".credentials"));
            File.WriteAllText(Path.Combine(installed, ".credentials", "local.dat"), "private-login-sentinel");
            Directory.CreateDirectory(Path.Combine(installed, ".state"));
            File.WriteAllText(Path.Combine(installed, ".state", "status-drafts.json"), "private-draft-sentinel");
            var staged = Task.Run(() => UpdateCore.Prepare(installed, feed, release, CancellationToken.None)).Result;
            check("update_package_download_verified_before_apply", UpdateCore.MatchesHash(staged, release.Sha256));
            bool rejectedExtra = false;
            var badRelease = new UpdateRelease { Application = release.Application, Version = release.Version, Package = release.Package };
            string badZip = Path.Combine(output, "update-extra-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create)) {
                foreach (var name in UpdateCore.RuntimeFiles) archive.CreateEntryFromFile(Path.Combine(appRoot, name), name);
                var entry = archive.CreateEntry("settings.json"); using (var writer = new StreamWriter(entry.Open())) writer.Write("overwrite");
            }
            badRelease.Sha256 = UpdateCore.HashFile(badZip);
            try { UpdateCore.Apply(installed, badZip, badRelease); } catch (InvalidDataException) { rejectedExtra = true; }
            check("update_rejects_settings_inside_package_without_touching_install", rejectedExtra &&
                File.ReadAllText(Path.Combine(installed, "CalendarWidget.exe")) == "old:CalendarWidget.exe" &&
                File.ReadAllText(Path.Combine(installed, "settings.json")) == "private-settings-sentinel");
            bool rolledBack = false;
            try { UpdateCore.Apply(installed, staged, release, 1); } catch (IOException) { rolledBack = true; }
            check("update_injected_failure_restores_all_program_files", rolledBack && UpdateCore.RuntimeFiles.All(name =>
                File.ReadAllText(Path.Combine(installed, name)) == "old:" + name));
            var backup = UpdateCore.Apply(installed, staged, release);
            check("update_applies_verified_program_files_only", File.ReadAllBytes(Path.Combine(installed, "CalendarWidget.exe")).SequenceEqual(File.ReadAllBytes(Path.Combine(appRoot, "CalendarWidget.exe"))) &&
                File.ReadAllText(Path.Combine(installed, "settings.json")) == "private-settings-sentinel" &&
                File.ReadAllText(Path.Combine(installed, ".credentials", "local.dat")) == "private-login-sentinel" &&
                File.ReadAllText(Path.Combine(installed, ".state", "status-drafts.json")) == "private-draft-sentinel");
            UpdateCore.Restore(installed, backup);
            check("update_backup_supports_explicit_restore", File.ReadAllText(Path.Combine(installed, "CalendarWidget.exe")) == "old:CalendarWidget.exe");
            File.AppendAllText(zip, "tamper");
            bool hashRejected = false;
            try { Task.Run(() => UpdateCore.Prepare(installed, feed, release, CancellationToken.None)).Wait(); }
            catch (AggregateException error) { hashRejected = error.InnerException is InvalidDataException; }
            check("update_rejects_tampered_package", hashRejected);
        }
        public static void RunUi(Widget widget, string output, Action<string, bool> check, Action<Window, string, double> capture)
        {
            var release = new UpdateRelease { Application = "CalendarWidget", Version = "1.1.3",
                Package = "CalendarWidget-v1.1.3.zip", Sha256 = new string('a', 64) };
            widget.ShowSyntheticUpdate(release);
            check("update_ui_shows_version_and_apply_button", widget.Find<Border>("UpdateBanner").Visibility == Visibility.Visible &&
                widget.Find<TextBlock>("UpdateSummary").Text.Contains("1.1.3") && widget.Find<Button>("UpdateApplyButton").IsEnabled);
            capture(widget.Window, Path.Combine(output, "update-available-100.png"), 96);
            widget.Find<Button>("UpdateDismissButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var originalFeed = widget.Configuration.ReleaseFeed;
            var settings = new SettingsDialog(widget); settings.Window.Show();
            settings.Find<TabControl>("ConfigTabs").SelectedIndex = 5;
            settings.Find<TextBox>("ReleaseFeed").Text = "https://example.invalid/releases/latest/download/release.json";
            settings.Window.UpdateLayout();
            check("update_settings_tab_is_visible", settings.Find<TextBox>("ReleaseFeed").IsVisible);
            capture(settings.Window, Path.Combine(output, "update-settings-100.png"), 96);
            settings.Find<Button>("CancelSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("update_settings_cancel_keeps_feed", widget.Configuration.ReleaseFeed == originalFeed);
            check("update_ui_dismiss_keeps_current_app", widget.Find<Border>("UpdateBanner").Visibility == Visibility.Collapsed);
            widget.ShowSyntheticUpdate(release);
            int calls = 0; var pending = new TaskCompletionSource<bool>();
            widget.SyntheticUpdateApply = item => { calls++; return pending.Task; };
            widget.Find<Button>("UpdateApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            widget.Find<Button>("UpdateApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("update_ui_repeated_click_starts_one_apply", calls == 1 && !widget.Find<Button>("UpdateApplyButton").IsEnabled);
            pending.SetResult(false);
            widget.Window.Dispatcher.Invoke(new Action(delegate { }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            check("update_ui_cancelled_apply_reenables_button", widget.Find<Button>("UpdateApplyButton").IsEnabled);
            widget.Find<Button>("UpdateDismissButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
