using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;

namespace CalendarWidget
{
    public static class UiWorkflowVerification
    {
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static T Visual<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root == null) return null;
            var own = root as T; if (own != null && own.Name == name) return own;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var found = Visual<T>(VisualTreeHelper.GetChild(root, i), name); if (found != null) return found; }
            return null;
        }
        private static void Wait(Task task, Window window)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
            }
            if (!task.IsCompleted) throw new Exception("UI task timeout"); task.GetAwaiter().GetResult(); window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.ApplicationIdle);
        }
        public static void Run(Widget widget, string output, Action<string, bool> check, Action<Window, string, double> capture)
        {
            var originalSource = widget.Source; string originalConfig = JsonData.Serialize(widget.Configuration);
            string originalSelectedCalendar = widget.Configuration.SelectedProfileId;
            Click(widget.Find<Button>("FilterButton"));
            var listedCalendars = widget.Find<ItemsControl>("CalendarChoices");
            check("widget_multiselect_lists_every_snapshot_calendar", listedCalendars.Items.Count == widget.Snapshot.Calendars.Count);
            Click(widget.Find<Button>("ClearCalendarsButton"));
            check("widget_clear_calendar_selection_hides_all_events", widget.Configuration.VisibleCalendarKeys.Count == 0 && widget.Find<ItemsControl>("EventList").Items.Count == 0);
            listedCalendars.UpdateLayout();
            var firstChoice = Visual<CheckBox>(listedCalendars, "CalendarChoiceCheck");
            if (firstChoice != null) { firstChoice.IsChecked = true; firstChoice.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); }
            check("widget_checkbox_selects_one_calendar_without_remote_changes", firstChoice != null && widget.Configuration.VisibleCalendarKeys.Count == 1 && widget.Configuration.VisibleCalendarKeys[0] == ((CalendarChoiceOption)firstChoice.DataContext).Id);
            Click(widget.Find<Button>("SelectAllCalendarsButton"));
            check("widget_select_all_restores_all_events", widget.Configuration.VisibleCalendarKeys.Count == widget.Snapshot.Calendars.Count);
            var savedKeys = widget.Configuration.VisibleCalendarKeys.ToList();
            var roundtripPath = Path.Combine(output, "calendar-visibility-" + Guid.NewGuid().ToString("N") + ".json");
            ConfigurationStore.Save(roundtripPath, widget.Configuration);
            check("widget_calendar_multiselect_persists_after_restart", ConfigurationStore.Load(roundtripPath).VisibleCalendarKeys.SequenceEqual(savedKeys));
            widget.Configuration.VisibleCalendarKeys = null; widget.Configuration.SelectedProfileId = originalSelectedCalendar; widget.Reload();
            Click(widget.Find<Button>("FilterButton"));
            var cancelled = new SettingsDialog(widget); cancelled.Window.Show(); cancelled.Find<TextBox>("AccountName").Text = "취소할 편집";
            Click(cancelled.Find<Button>("CancelSettingsButton"));
            check("settings_cancel_discards_edits", JsonData.Serialize(widget.Configuration) == originalConfig);
            var close = new SettingsDialog(widget); close.Window.Show(); close.Find<TextBox>("AccountName").Text = "닫을 편집"; close.Window.Close();
            check("settings_window_close_discards_edits", JsonData.Serialize(widget.Configuration) == originalConfig);
            var guard = new SettingsDialog(widget); guard.Window.Show(); Click(guard.Find<Button>("ConnectGoogleButton")); Wait(guard.CurrentAction, guard.Window);
            check("google_connect_ui_explains_new_login_disabled", guard.Find<TextBlock>("SettingsStatus").Text.Contains("새 Google 로그인") && !guard.Find<TextBlock>("SettingsStatus").Text.Contains("승인 대기"));
            Click(guard.Find<Button>("ConnectMicrosoftButton")); Wait(guard.CurrentAction, guard.Window);
            check("microsoft_connect_ui_waits_for_approval", guard.Find<TextBlock>("SettingsStatus").Text.Contains("승인 대기"));
            guard.Find<PasswordBox>("SourceSharingLink").Password = "https://wrong-host.invalid/private-must-not-leak";
            Click(guard.Find<Button>("ReadSourceButton")); Wait(guard.CurrentAction, guard.Window);
            check("invalid_link_ui_error_does_not_reveal_link", guard.Find<TextBlock>("SettingsStatus").Text.Contains("완료하지") && !guard.Find<TextBlock>("SettingsStatus").Text.Contains("wrong-host")); guard.Window.Close();
            var linked = new AccountConfiguration { Id = "synthetic-linked", DisplayName = "Synthetic Google", Email = "linked@example.invalid", VerifiedEmail = "linked@example.invalid", GoogleSubject = "synthetic-subject", TargetCalendarId = "owned-calendar" };
            widget.Configuration.Accounts.Add(linked);
            var existing = new SettingsDialog(widget);
            int existingReads = 0;
            existing.SyntheticCalendarReader = token => { existingReads++; return Task.FromResult(new List<RemoteCalendar> { new RemoteCalendar { Id = "owned-calendar", Name = "Synthetic owned", AccessRole = "owner" } }); };
            existing.Window.Show();
            existing.Find<ComboBox>("AccountSelect").SelectedItem = existing.Find<ComboBox>("AccountSelect").Items.OfType<AccountConfiguration>().Single(a => a.Id == linked.Id);
            check("saved_account_label_does_not_claim_unconnected", !linked.ToString().Contains("미연결") && existing.Find<TextBlock>("ConnectionStatus").Text.Contains("확인 전"));
            Click(existing.Find<Button>("ConnectGoogleButton")); Wait(existing.CurrentAction, existing.Window);
            check("existing_grant_button_reads_calendar_list_without_new_login", existingReads == 1 && existing.Find<TextBlock>("ConnectionStatus").Text.Contains("읽기 확인됨") && existing.Find<ComboBox>("TargetCalendarSelect").Items.Count == 1);
            existing.SyntheticCalendarReader = token => { throw new OAuthExchangeException(400, "invalid_grant", "synthetic"); };
            Click(existing.Find<Button>("LoadCalendarsButton")); Wait(existing.CurrentAction, existing.Window);
            check("invalid_grant_shows_explicit_reconnect_need", existing.Find<TextBlock>("ConnectionStatus").Text.Contains("재연결 필요") && existing.Find<Button>("ConnectGoogleButton").Content.ToString().Contains("로그인"));
            Click(existing.Find<Button>("ConnectGoogleButton")); Wait(existing.CurrentAction, existing.Window);
            check("cached_mode_never_starts_new_login", existing.Find<TextBlock>("SettingsStatus").Text.Contains("새 Google 로그인") && existingReads == 1);
            existing.Window.Close(); widget.Configuration.Accounts.Remove(linked);
            var manual = new SettingsDialog(widget); manual.Window.Show(); manual.Find<TabControl>("ConfigTabs").SelectedIndex = 2;
            manual.Window.UpdateLayout(); manual.Window.Dispatcher.Invoke(new Action(delegate {}), DispatcherPriority.ApplicationIdle);
            check("manual_download_flow_visible_microsoft_options_collapsed", manual.Find<Button>("OpenSourceButton").IsVisible && manual.Find<Button>("BrowseWorkbookButton").IsVisible && !manual.Find<TextBox>("MicrosoftClientId").IsVisible);
            string opened = null; manual.SyntheticOpenSource = link => opened = link; manual.Find<PasswordBox>("SourceSharingLink").Password = "https://synthetic.sharepoint.com/:x:/fixture";
            Click(manual.Find<Button>("OpenSourceButton"));
            check("source_open_uses_only_validated_official_link", opened == "https://synthetic.sharepoint.com/:x:/fixture");
            manual.Window.Close();
            var fixture = Path.Combine(output, "synthetic", "ui-events.xlsx");
            var rows = new List<Dictionary<string, string>> { new Dictionary<string, string> { { "A", "이벤트ID" }, { "B", "이벤트명" }, { "C", "시작" }, { "D", "종료" } } };
            for (int i = 0; i < 10; i++) rows.Add(new Dictionary<string, string> { { "A", "ui-source-" + i }, { "B", "합성 UI 일정 " + i }, { "C", "2026-10-05T10:" + (i + 10) + ":00+09:00" }, { "D", "2026-10-05T10:" + (i + 11) + ":00+09:00" } });
            SyntheticXlsx.Write(fixture, rows, false);
            var offline = new SettingsDialog(widget); offline.Window.Show(); offline.Find<TabControl>("ConfigTabs").SelectedIndex = 2;
            offline.Find<ComboBox>("SourceKindSelect").SelectedIndex = 1; offline.Find<TextBox>("SourceWorkbookPath").Text = fixture; offline.Find<TextBox>("TargetCalendarId").Text = "ui-calendar";
            Click(offline.Find<Button>("ReadSourceButton")); Wait(offline.CurrentAction, offline.Window);
            check("xlsx_read_button_opens_offline_preview", offline.LastPreview != null && offline.LastPreview.Plan.Items.Count == 10 && !offline.LastPreview.Plan.RemoteInspected);
            check("offline_sync_button_remains_disabled", !offline.LastPreview.Find<Button>("ExecuteSyncButton").IsEnabled);
            capture(offline.LastPreview.Window, Path.Combine(output, "preview-offline.png"), 96);
            Click(offline.LastPreview.Find<Button>("BackButton"));
            check("preview_back_returns_to_settings", offline.Window.IsVisible && !offline.LastPreview.Window.IsVisible);
            Click(offline.Find<Button>("CancelSettingsButton"));
            check("read_then_cancel_preserves_widget_and_settings", widget.Source == originalSource && JsonData.Serialize(widget.Configuration) == originalConfig);
            var pending = new SettingsDialog(widget); pending.Window.Show(); pending.Find<ComboBox>("SourceKindSelect").SelectedIndex = 1;
            var completion = new TaskCompletionSource<WorkbookEvents>(); int pendingReads = 0; pending.SyntheticReader = (config, token) => { pendingReads++; token.Register(() => completion.TrySetCanceled()); return completion.Task; };
            Click(pending.Find<Button>("ReadSourceButton")); var pendingAction = pending.CurrentAction; Click(pending.Find<Button>("ReadSourceButton"));
            check("repeated_read_click_while_busy_starts_one_read", pendingReads == 1 && Object.ReferenceEquals(pending.CurrentAction, pendingAction));
            pending.Window.Close(); Wait(pending.CurrentAction, widget.Window);
            check("close_cancels_pending_source_read_without_commit", !pending.Window.IsVisible && widget.Source == originalSource && pending.LastPreview == null);
            var fake = new FakeCalendarGateway(); var readOptions = new ExcelReadOptions { DocumentId = widget.Configuration.SourceDocumentId }; var data = ExcelEventParser.Read(fixture, readOptions);
            foreach (var source in data.Events) fake.Seed("ui-calendar", new RemoteEvent { Id = "ui-existing-" + source.ExplicitId, Summary = source.Title, Start = source.Start, End = source.End.Value, ETag = "v1", ColorId = "" });
            var connected = new SettingsDialog(widget); connected.Window.Show(); connected.SyntheticGateway = fake;
            connected.Find<ComboBox>("SourceKindSelect").SelectedIndex = 1; connected.Find<TextBox>("SourceWorkbookPath").Text = fixture; connected.Find<TextBox>("TargetCalendarId").Text = "ui-calendar";
            Click(connected.Find<Button>("ReadSourceButton")); Wait(connected.CurrentAction, connected.Window);
            var preview = connected.LastPreview;
            Click(connected.Find<Button>("SyncButton")); Wait(connected.CurrentAction, connected.Window);
            check("repeated_preview_click_reuses_visible_preview", Object.ReferenceEquals(preview, connected.LastPreview) && connected.Window.OwnedWindows.Count == 1);
            check("mock_connected_preview_adopts_ten_existing", preview.Plan.Items.Count == 10 && preview.Plan.Items.All(i => i.Action == "채택" || i.Action == "유지"));
            check("preview_requires_review_before_execution", !preview.Find<Button>("ExecuteSyncButton").IsEnabled);
            preview.Find<CheckBox>("ReviewWarningsCheck").IsChecked = true;
            check("reviewed_mock_preview_enables_explicit_sync", preview.Find<Button>("ExecuteSyncButton").IsEnabled);
            capture(preview.Window, Path.Combine(output, "preview-mock-adoption.png"), 96);
            var previewAppearance = new WidgetConfiguration { ThemeMode = "dark", DisplayFontSize = 18 };
            ThemeManager.Apply(preview.Window, previewAppearance);
            capture(preview.Window, Path.Combine(output, "preview-dark-large-100.png"), 96);
            check("preview_dark_grid_text_remains_visible", preview.Window.FontSize == 18 && preview.Find<DataGrid>("PlanGrid").Foreground.ToString() != preview.Find<DataGrid>("PlanGrid").Background.ToString());
            ThemeManager.ApplyFromOwner(preview.Window, connected.Window);
            Click(preview.Find<Button>("ExecuteSyncButton")); Wait(preview.CurrentAction, preview.Window);
            check("ui_sync_adopts_and_rechecks_without_creating", fake.Creates == 0 && fake.Updates == 0 && preview.Plan.Items.All(i => i.Action == "유지"));
            Click(preview.Find<Button>("ExecuteSyncButton")); Wait(preview.CurrentAction, preview.Window);
            check("ui_second_sync_keeps_ten_no_duplicates", fake.Count("ui-calendar") == 10 && fake.Creates == 0 && fake.Updates == 0);
            check("mapping_controls_reenabled_after_sync", preview.Find<Button>("ImportMappingButton").IsEnabled && preview.Find<Button>("BindIdentityButton").IsEnabled);
            var unconfirmed = new WorkbookEvents { DocumentId = "synthetic-bulk", FileHash = "synthetic-bulk-hash" };
            for (int n = 0; n < 3; n++) unconfirmed.Events.Add(new SourceEvent { Alias = "bulk-" + n, Title = "Synthetic new " + n, Phase = "EVENT",
                Start = DateTimeOffset.Parse("2026-10-20T10:00:00+09:00").AddHours(n), End = DateTimeOffset.Parse("2026-10-20T11:00:00+09:00").AddHours(n) });
            unconfirmed.Events.Add(new SourceEvent { Alias = "bulk-ambiguous", Title = "Synthetic ambiguous", Phase = "EVENT",
                Start = DateTimeOffset.Parse("2026-10-21T10:00:00+09:00"), End = DateTimeOffset.Parse("2026-10-21T11:00:00+09:00") });
            var bulkRepo = new SyncRepository(Path.Combine(output, "bulk-preview-" + Guid.NewGuid().ToString("N") + ".json")); bulkRepo.ResolveIdentities(unconfirmed);
            var bulkEngine = new SyncEngine(bulkRepo); var bulkGateway = new FakeCalendarGateway();
            foreach (var id in new[] { "existing-one", "existing-two" }) bulkGateway.Seed("synthetic-new-calendar", new RemoteEvent { Id = id, Summary = "Synthetic ambiguous",
                Start = DateTimeOffset.Parse("2026-10-21T10:00:00+09:00"), End = DateTimeOffset.Parse("2026-10-21T11:00:00+09:00"), ETag = "v1" });
            var bulkPlan = bulkEngine.Plan(unconfirmed, "synthetic-account", "synthetic-new-calendar", bulkGateway.List("synthetic-new-calendar", DateTimeOffset.MinValue, CancellationToken.None).Result, "", DateTimeOffset.Parse("2026-10-06T10:00:00+09:00"));
            var bulkPreview = new PreviewDialog(widget.Window, widget.Root, bulkRepo, bulkEngine, bulkPlan, bulkGateway, token => Task.FromResult(unconfirmed), true, DateTimeOffset.Parse("2026-10-06T10:00:00+09:00"));
            bulkPreview.Window.Show();
            check("bulk_preview_three_only_confirmable", bulkPreview.Plan.Items.Count(i => i.NewConfirmationAvailable) == 3 && bulkPreview.Plan.Items.Count(i => !i.NewConfirmationAvailable && i.Reason.Contains("여러 개")) == 1 && !bulkPreview.Find<Button>("ConfirmNewButton").IsEnabled);
            Click(bulkPreview.Find<Button>("SelectConfirmableButton"));
            check("bulk_select_all_and_count", bulkPreview.Find<DataGrid>("PlanGrid").SelectedItems.Count == 3 && bulkPreview.Find<TextBlock>("NewConfirmationCount").Text.Contains("선택 3건"));
            Click(bulkPreview.Find<Button>("ClearPlanSelectionButton"));
            check("bulk_clear_selection_no_write", bulkPreview.Find<DataGrid>("PlanGrid").SelectedItems.Count == 0 && !bulkPreview.Find<Button>("ConfirmNewButton").IsEnabled && bulkGateway.Creates == 0);
            Click(bulkPreview.Find<Button>("SelectConfirmableButton")); Click(bulkPreview.Find<Button>("ConfirmNewButton"));
            check("bulk_confirmation_replans_without_remote_write", bulkPreview.Plan.Items.Count(i => i.Action == "신규") == 3 && bulkPreview.Plan.Items.All(i => !i.NewConfirmationAvailable) && bulkGateway.Creates == 0);
            check("bulk_confirmation_scoped_to_exact_target", bulkEngine.Plan(unconfirmed, "synthetic-account", "different-calendar", new List<RemoteEvent>(), "", DateTimeOffset.Parse("2026-10-06T10:00:00+09:00")).Items.Count(i => i.NewConfirmationAvailable) == 4);
            Click(bulkPreview.Find<Button>("SelectConfirmableButton"));
            check("bulk_confirmation_repeat_has_no_candidates", bulkPreview.Find<DataGrid>("PlanGrid").SelectedItems.Count == 0 && !bulkPreview.Find<Button>("ConfirmNewButton").IsEnabled);
            bulkPreview.Window.Close();
            Click(preview.Find<Button>("BackButton")); Click(connected.Find<Button>("SaveSettingsButton"));
            check("save_read_source_updates_widget_with_sample_label", widget.Source != originalSource && widget.Find<TextBlock>("ModeTitle").Text.Contains("샘플") && widget.LastWorkbook.Events.Count == 10);
            Wait(widget.RefreshSource(), widget.Window);
            check("widget_refresh_reads_selected_xlsx_without_google_write", widget.LastWorkbook.FileHash == SyncHash.Bytes(File.ReadAllBytes(fixture)) && fake.Creates == 0 && fake.Updates == 0);
            File.SetLastWriteTimeUtc(fixture, DateTime.UtcNow.AddDays(-3)); Wait(widget.RefreshSource(), widget.Window);
            check("older_download_shows_timestamp_and_latest_file_warning", widget.LastWorkbook.FileModifiedAt.HasValue && widget.LastWorkbook.Warnings.Any(w => w.Contains("24시간")) && widget.LastWorkbook.Warnings.Any(w => w.Contains("서버 최신성")));
            File.SetLastWriteTimeUtc(fixture, DateTime.UtcNow);
            capture(widget.Window, Path.Combine(output, "widget-synthetic-xlsx.png"), 96);
            widget.Source = originalSource; widget.Reload();
            var failed = new SettingsDialog(widget); failed.Window.Show(); failed.Find<ComboBox>("SourceKindSelect").SelectedIndex = 1; failed.Find<TextBox>("SourceWorkbookPath").Text = Path.Combine(output, "absent.xlsx");
            Click(failed.Find<Button>("ReadSourceButton")); Wait(failed.CurrentAction, failed.Window);
            check("missing_file_ui_fails_without_fabricated_preview", failed.LastPreview == null && failed.Find<TextBlock>("SettingsStatus").Text.Contains("XLSX")); failed.Window.Close();
            var accountSwitch = new SettingsDialog(widget); accountSwitch.Window.Show(); Click(accountSwitch.Find<Button>("AddAccountButton"));
            accountSwitch.Find<TextBox>("AccountEmail").Text = "synthetic-two@example.invalid"; accountSwitch.Find<TextBox>("TargetCalendarId").Text = "cal-two";
            accountSwitch.Find<ComboBox>("AccountSelect").SelectedIndex = 0;
            check("account_switch_keeps_target_separate", accountSwitch.Find<TextBox>("TargetCalendarId").Text != "cal-two"); accountSwitch.Window.Close();
            var category = new SettingsDialog(widget); category.Window.Show();
            category.SyntheticCalendarReader = token => Task.FromResult(new List<RemoteCalendar> {
                new RemoteCalendar { Id = "owned-personal", Name = "합성 개인", AccessRole = "owner" },
                new RemoteCalendar { Id = "owned-work", Name = "합성 업무", AccessRole = "owner" },
                new RemoteCalendar { Id = "shared-writer", Name = "합성 공유", AccessRole = "writer" } });
            Click(category.Find<Button>("LoadCalendarsButton")); Wait(category.CurrentAction, category.Window);
            var targets = category.Find<ComboBox>("TargetCalendarSelect");
            check("target_selection_lists_owner_and_writer_calendars", targets.Items.Count == 3 && targets.Items.OfType<RemoteCalendar>().All(CalendarAccess.CanWrite));
            targets.SelectedValue = "owned-personal"; targets.SelectedValue = "owned-work";
            check("category_named_existing_calendar_can_be_selected_as_single_target", category.Find<TextBox>("TargetCalendarId").Text == "owned-work");
            category.Window.Close();
            string beforeProfileConfig = JsonData.Serialize(widget.Configuration);
            widget.Configuration.Accounts.Add(linked);
            var targetSettings = new SettingsDialog(widget); targetSettings.Window.Show(); targetSettings.Find<TabControl>("ConfigTabs").SelectedIndex = 2;
            targetSettings.Find<ComboBox>("SourceAccountSelect").SelectedItem = targetSettings.Find<ComboBox>("SourceAccountSelect").Items.OfType<AccountConfiguration>().Single(a => a.Id == linked.Id);
            targetSettings.SyntheticCalendarReader = token => Task.FromResult(new List<RemoteCalendar> {
                new RemoteCalendar { Id = "first-writer", Name = "First", AccessRole = "writer" },
                new RemoteCalendar { Id = "second-writer", Name = "Second", AccessRole = "writer" },
                new RemoteCalendar { Id = "reader-only", Name = "Reader", AccessRole = "reader" } });
            Click(targetSettings.Find<Button>("LoadSourceCalendarsButton")); Wait(targetSettings.CurrentAction, targetSettings.Window);
            check("source_target_selector_lists_only_writer_owner", targetSettings.Find<ComboBox>("SourceCalendarSelect").Items.Count == 2);
            targetSettings.Find<ComboBox>("SourceCalendarSelect").SelectedValue = "second-writer";
            targetSettings.Find<ComboBox>("SourceKindSelect").SelectedIndex = 1; targetSettings.Find<TextBox>("SourceWorkbookPath").Text = fixture;
            targetSettings.SyntheticReader = (captured, token) => Task.FromResult(ExcelEventParser.Read(fixture, new ExcelReadOptions { DocumentId = captured.SourceDocumentId, Worksheet = captured.SourceWorksheet }));
            var targetGateway = new FakeCalendarGateway(); targetSettings.SyntheticGateway = targetGateway;
            Click(targetSettings.Find<Button>("ReadSourceButton")); Wait(targetSettings.CurrentAction, targetSettings.Window);
            check("source_preview_locks_exact_selected_target_without_writes", targetSettings.LastPreview != null && targetSettings.LastPreview.Plan.CalendarId == "second-writer" && targetGateway.Creates == 0 && targetGateway.Updates == 0 && !targetSettings.Find<ComboBox>("SourceCalendarSelect").IsEnabled);
            Click(targetSettings.LastPreview.Find<Button>("BackButton"));
            targetSettings.Find<ComboBox>("SourceCalendarSelect").SelectedValue = "first-writer";
            check("changing_source_target_invalidates_prior_preview", targetSettings.LastPreview == null && !targetSettings.Find<Button>("SyncButton").IsEnabled);
            Click(targetSettings.Find<Button>("ReadSourceButton")); Wait(targetSettings.CurrentAction, targetSettings.Window);
            check("source_target_change_replans_only_new_calendar", targetSettings.LastPreview != null && targetSettings.LastPreview.Plan.CalendarId == "first-writer" && targetGateway.Creates == 0 && targetGateway.Updates == 0);
            Click(targetSettings.LastPreview.Find<Button>("BackButton")); Click(targetSettings.Find<Button>("CancelSettingsButton"));
            var profiles = new SettingsDialog(widget); profiles.Window.Show(); profiles.Find<TabControl>("ConfigTabs").SelectedIndex = 1;
            profiles.SyntheticCalendarReader = token => Task.FromResult(new List<RemoteCalendar> {
                new RemoteCalendar { Id = "owned-calendar", Name = "Synthetic Sample", AccessRole = "owner" },
                new RemoteCalendar { Id = "shared-writer", Name = "Sample Secondary", AccessRole = "writer" },
                new RemoteCalendar { Id = "reader-only", Name = "Read only", AccessRole = "reader" } });
            var previousProfileCount = profiles.Find<ComboBox>("ConnectionProfileSelect").Items.Count;
            Click(profiles.Find<Button>("AddConnectionButton"));
            profiles.Find<ComboBox>("ProfileAccountSelect").SelectedItem = profiles.Find<ComboBox>("ProfileAccountSelect").Items.OfType<AccountConfiguration>().Single(a => a.Id == linked.Id);
            Click(profiles.Find<Button>("LoadProfileCalendarsButton")); Wait(profiles.CurrentAction, profiles.Window);
            check("direct_profile_lists_readable_owner_writer_reader", profiles.Find<ComboBox>("ProfileCalendarSelect").Items.Count == 3);
            profiles.Find<ComboBox>("ProfileCalendarSelect").SelectedValue = "shared-writer";
            profiles.Find<TextBox>("ProfileDisplayName").Text = "Kitchen";
            check("settings_add_direct_profile_preserves_existing_excel_profile", profiles.Find<ComboBox>("ConnectionProfileSelect").Items.Count == previousProfileCount + 1 && profiles.Find<StackPanel>("ProfileExcelFields").Visibility == Visibility.Collapsed);
            capture(profiles.Window, Path.Combine(output, "settings-calendar-profiles.png"), 96);
            profiles.Find<ComboBox>("ProfileModeSelect").SelectedIndex = 1;
            Click(profiles.Find<Button>("LoadProfileCalendarsButton")); Wait(profiles.CurrentAction, profiles.Window);
            check("excel_profile_lists_only_writable_calendars", profiles.Find<ComboBox>("ProfileCalendarSelect").Items.Count == 2 && !profiles.Find<ComboBox>("ProfileCalendarSelect").Items.OfType<RemoteCalendar>().Any(c => c.Id == "reader-only"));
            profiles.Find<TextBox>("ProfileWorkbookPath").Text = fixture;
            profiles.Find<TextBox>("ProfileWorksheet").Text = widget.Configuration.SourceWorksheet;
            var profileGateway = new FakeCalendarGateway();
            foreach (var source in data.Events) profileGateway.Seed("shared-writer", new RemoteEvent { Id = "profile-existing-" + source.ExplicitId, Summary = source.Title, Start = source.Start, End = source.End.Value, ETag = "v1" });
            profiles.SyntheticGateway = profileGateway;
            profiles.SyntheticReader = (captured, token) => Task.FromResult(ExcelEventParser.Read(fixture, new ExcelReadOptions { DocumentId = captured.SourceDocumentId, Worksheet = captured.SourceWorksheet }));
            Click(profiles.Find<Button>("ProfilePreviewButton")); Wait(profiles.CurrentAction, profiles.Window);
            var profilePreview = profiles.LastPreview;
            check("profile_excel_preview_uses_selected_calendar_and_isolated_document", profilePreview != null && profilePreview.Plan.CalendarId == "shared-writer" && profilePreview.Plan.Workbook.Events.Count == 10 && profilePreview.Plan.Scope == SyncEngine.Scope(linked.GoogleSubject, "shared-writer", profilePreview.Plan.Workbook.DocumentId) && profileGateway.Creates == 0 && profileGateway.Updates == 0);
            profilePreview.Find<CheckBox>("ReviewWarningsCheck").IsChecked = true;
            Click(profilePreview.Find<Button>("ExecuteSyncButton")); Wait(profilePreview.CurrentAction, profilePreview.Window);
            int profileMappingCount = new SyncRepository(Path.Combine(widget.Root, "evidence", widget.VerificationStateFile)).State.Mappings.Count(m => m.Scope == profilePreview.Plan.Scope);
            check("profile_explicit_mock_sync_maps_only_selected_calendar_without_writes", profileGateway.Count("shared-writer") == 10 && profileGateway.Creates == 0 && profileGateway.Updates == 0 && profileMappingCount == 10 && profilePreview.Find<TextBlock>("PreviewStatus").Text.Length > 0);
            Click(profilePreview.Find<Button>("BackButton"));
            Click(profiles.Find<Button>("AddConnectionButton"));
            Click(profiles.Find<Button>("AddConnectionButton"));
            check("settings_ui_allows_three_or_more_calendar_connections", profiles.Find<ComboBox>("ConnectionProfileSelect").Items.Count == previousProfileCount + 3);
            Click(profiles.Find<Button>("CancelSettingsButton")); widget.Configuration.Accounts.Remove(linked);
            check("profile_cancel_keeps_existing_config_and_mappings", JsonData.Serialize(widget.Configuration) == beforeProfileConfig);
            var cancelRepo = new SyncRepository(Path.Combine(output, "ui-cancel-" + Guid.NewGuid().ToString("N") + ".json")); cancelRepo.ResolveIdentities(data);
            var cancelEngine = new SyncEngine(cancelRepo); var cancelPlan = cancelEngine.Plan(data, "synthetic", "ui-calendar", fake.List("ui-calendar", widget.Now, CancellationToken.None).Result, "", widget.Now);
            int rereads = 0; var syncGate = new TaskCompletionSource<WorkbookEvents>();
            var cancelPreview = new PreviewDialog(widget.Window, widget.Root, cancelRepo, cancelEngine, cancelPlan, fake, token => { rereads++; token.Register(() => syncGate.TrySetCanceled()); return syncGate.Task; }, true, widget.Now);
            cancelPreview.Window.Show(); cancelPreview.Find<CheckBox>("ReviewWarningsCheck").IsChecked = true;
            Click(cancelPreview.Find<Button>("ExecuteSyncButton")); var executing = cancelPreview.CurrentAction; Click(cancelPreview.Find<Button>("ExecuteSyncButton"));
            check("double_sync_click_while_busy_starts_one_execution", rereads == 1 && Object.ReferenceEquals(executing, cancelPreview.CurrentAction));
            cancelPreview.Window.Close(); Wait(cancelPreview.CurrentAction, widget.Window);
            check("closing_preview_cancels_before_write_and_preserves_journal", fake.Creates == 0 && fake.Updates == 0 && cancelRepo.State.Journal.Count == 0);
            var choices = new List<ManualCalendarChoice> { new ManualCalendarChoice { AccountSubject = "synthetic-subject", CalendarId = "writer-one", Label = "Writer (one@example.invalid)" } };
            var editor = new ManualEventEditor(widget.Window, widget.Root, new DateTime(2026, 10, 8), choices); editor.Window.Show();
            editor.Find<Expander>("ManualStatusExpander").IsExpanded = true;
            editor.Find<TextBox>("ManualStatusSetup").Text = "Visible status input";
            editor.Window.UpdateLayout();
            var statusBox = editor.Find<TextBox>("ManualStatusSetup");
            check("manual_status_text_has_dark_foreground_on_white_background", ((SolidColorBrush)statusBox.Foreground).Color == Color.FromRgb(0x26, 0x22, 0x30) &&
                ((SolidColorBrush)statusBox.Background).Color == Colors.White && ((SolidColorBrush)editor.CalendarSelect.Foreground).Color == Color.FromRgb(0x26, 0x22, 0x30));
            ((ScrollViewer)editor.Window.Content).ScrollToBottom();
            capture(editor.Window, Path.Combine(output, "manual-status-readable-100.png"), 96);
            capture(editor.Window, Path.Combine(output, "manual-status-readable-200.png"), 192);
            int confirms = 0, submits = 0, refreshes = 0;
            editor.ConfirmCreate = input => { confirms++; return true; };
            editor.ConfirmDiscard = () => false;
            editor.Submit = input => { submits++; return Task.FromResult(new ManualEventRecord { EventId = "aaccafe" + new string('a', 32), State = "complete", Message = "confirmed" }); };
            editor.Refresh = () => { refreshes++; return Task.FromResult(0); };
            editor.Find<TextBox>("ManualTitle").Text = "Synthetic registration";
            check("manual_editor_shows_exact_account_calendar_and_seoul_fields", editor.CalendarSelect.Items.Count == 1 && ((ManualCalendarChoice)editor.CalendarSelect.SelectedItem).CalendarId == "writer-one" && editor.Find<DatePicker>("ManualStartDate").SelectedDate.Value.Date == new DateTime(2026, 10, 8));
            editor.Window.Close(); check("manual_editor_keeps_unsaved_input_on_rejected_discard", editor.Window.IsVisible);
            capture(editor.Window, Path.Combine(output, "manual-event-editor.png"), 96);
            Click(editor.SaveButton); Wait(editor.CurrentAction, editor.Window);
            check("manual_editor_explicit_confirm_submits_once_and_refreshes", confirms == 1 && submits == 1 && refreshes == 1 && !editor.SaveButton.IsEnabled);
            check("manual_status_locked_input_remains_readable", statusBox.IsReadOnly && ((SolidColorBrush)statusBox.Foreground).Color == Color.FromRgb(0x68, 0x62, 0x72) &&
                ((SolidColorBrush)statusBox.Background).Color == Color.FromRgb(0xF7, 0xF5, 0xFA));
            editor.Window.Close(); check("manual_editor_closes_after_confirmed_save", !editor.Window.IsVisible);
            var pendingEditor = new ManualEventEditor(widget.Window, widget.Root, new DateTime(2026, 10, 8), choices); pendingEditor.Window.Show();
            pendingEditor.Find<TextBox>("ManualTitle").Text = "Pending synthetic"; pendingEditor.ConfirmCreate = input => true;
            var gate = new TaskCompletionSource<ManualEventRecord>(); int pendingSubmits = 0;
            pendingEditor.Submit = input => { pendingSubmits++; return gate.Task; };
            Click(pendingEditor.SaveButton); var firstAction = pendingEditor.CurrentAction; Click(pendingEditor.SaveButton);
            pendingEditor.Window.Close();
            check("manual_editor_blocks_duplicate_click_and_close_during_write", pendingSubmits == 1 && Object.ReferenceEquals(firstAction, pendingEditor.CurrentAction) && pendingEditor.Window.IsVisible);
            gate.SetResult(new ManualEventRecord { State = "pending", Message = "check later" }); Wait(firstAction, pendingEditor.Window);
            check("manual_pending_retry_locks_event_identity", pendingEditor.SaveButton.IsEnabled && !pendingEditor.CalendarSelect.IsEnabled && pendingEditor.Find<TextBox>("ManualTitle").IsReadOnly);
            pendingEditor.ConfirmDiscard = () => true; pendingEditor.Window.Close();
        }
    }
}

