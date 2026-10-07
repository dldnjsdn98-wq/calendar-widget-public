using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CalendarWidget
{
    public static class CalendarChangeWatchVerification
    {
        private static CalendarEvent Event(string id, string title, DateTimeOffset start, Dictionary<string, string> status = null)
        {
            return new CalendarEvent { Id = "synthetic-calendar:" + id, CalendarId = "synthetic-calendar", Title = title,
                Start = start.ToString("o"), End = start.AddHours(1).ToString("o"), Origin = "google-direct",
                Fields = new Dictionary<string, string>(), RemoteStatus = status };
        }
        private static CalendarSnapshot Snapshot(params CalendarEvent[] events)
        {
            return new CalendarSnapshot { SchemaVersion = 1, Mode = "google", TimeZone = "Asia/Seoul", ObservedAt = DateTimeOffset.UtcNow.ToString("o"), Calendars = new List<CalendarProfile> {
                new CalendarProfile { Id = "synthetic-calendar", Name = "Synthetic shared calendar", Color = "#9569D5", Fields = new List<FieldDefinition>(), ReadSucceeded = true } },
                Events = events.ToList() };
        }
        public static void Run(string output, Action<string, bool> check)
        {
            var now = DateTimeOffset.Parse("2026-10-06T10:00:00+09:00");
            var first = Event("one", "Synthetic item", now.AddDays(1));
            var clientA = new CalendarChangeTracker(); var clientB = new CalendarChangeTracker();
            var firstA = clientA.Accept(Snapshot(first), now);
            var firstB = clientB.Accept(Snapshot(Event("one", "Synthetic item", now.AddDays(1))), now);
            check("watch_two_clients_first_read_is_silent", firstA.Total == 0 && firstB.Total == 0 && firstA.Entries.Count == 0 && firstB.Entries.Count == 0);
            var remoteStatus = GoogleOnlyStatus.Keys.ToDictionary(k => k, k => k == "setup" ? "remote complete" : "");
            var changed = Event("one", "Synthetic item", now.AddDays(1), remoteStatus);
            var statusChange = clientB.Accept(Snapshot(changed), now.AddMinutes(5));
            check("watch_detail_has_calendar_event_before_after_and_time", statusChange.Entries.Count == 1 && statusChange.Entries[0].CalendarName == "Synthetic shared calendar" &&
                statusChange.Entries[0].EventName == "Synthetic item" && statusChange.Entries[0].DetectedAt == now.AddMinutes(5) &&
                statusChange.Entries[0].Fields.Any(f => f.Label == "세팅" && f.Before == "확인 불가" && f.After == "remote complete"));
            remoteStatus["setup"] = "mutated after detection";
            check("watch_detail_values_are_snapshot_copies", statusChange.Entries[0].Fields.Any(f => f.Label == "세팅" && f.After == "remote complete"));
            remoteStatus["setup"] = "remote complete";
            check("watch_second_client_detects_remote_status_once", statusChange.Updated == 1 &&
                clientB.Accept(Snapshot(Event("one", "Synthetic item", now.AddDays(1), new Dictionary<string, string>(remoteStatus))), now.AddMinutes(10)).Total == 0);
            clientA.Suppress(first.Id, now.AddMinutes(4));
            check("watch_local_write_suppressed_once", clientA.Accept(Snapshot(Event("one", "Synthetic item", now.AddDays(1), remoteStatus)), now.AddMinutes(5)).Total == 0);
            check("watch_later_external_change_not_suppressed", clientA.Accept(Snapshot(Event("one", "Synthetic revised", now.AddDays(1), remoteStatus)), now.AddMinutes(6)).Updated == 1);
            var added = Event("two", "Synthetic new item", now.AddDays(2));
            var addRemove = clientB.Accept(Snapshot(added), now.AddMinutes(15));
            check("watch_add_and_cancel_counts_future_events", addRemove.Added == 1 && addRemove.Removed == 1);
            check("watch_add_and_cancel_have_before_after_rows", addRemove.Entries.Count == 2 &&
                addRemove.Entries.Any(e => e.Kind == "추가" && e.EventName == "Synthetic new item" && e.Fields.Any(f => f.Label == "일정 이름" && f.Before == "—" && f.After == "Synthetic new item")) &&
                addRemove.Entries.Any(e => e.Kind == "취소" && e.EventName == "Synthetic item" && e.Fields.Any(f => f.Label == "일정 이름" && f.Before == "Synthetic item" && f.After == "—")));
            var duplicate = CalendarChangeDetail.Create(new CalendarProfile { Id = "second", Name = "Synthetic shared calendar", AccountName = "second@example.invalid" }, null, added, now, true);
            check("watch_same_name_calendar_uses_account_label", duplicate.CalendarName.Contains("second@example.invalid"));
            var allOld = Event("all", "Synthetic fields", now.AddDays(1), GoogleOnlyStatus.Keys.ToDictionary(k => k, k => "old " + k));
            var allNew = Event("all", "Synthetic fields", now.AddDays(1).AddHours(2), GoogleOnlyStatus.Keys.ToDictionary(k => k, k => "new " + k));
            var allDetail = CalendarChangeDetail.Create(Snapshot(allNew).Calendars[0], allOld, allNew, now, false);
            check("watch_all_five_status_cells_and_time_show_before_after", allDetail.Fields.Count(f => new[] { "세팅", "확인 · 알파", "알파와 테스터 · 테스터", "확인 · 리얼", "리얼과 테스터 · 테스터" }.Contains(f.Label) && f.Before.StartsWith("old ") && f.After.StartsWith("new ")) == 5 &&
                allDetail.Fields.Any(f => f.Label == "시작" && f.Before.Contains("한국 시간") && f.After.Contains("한국 시간")));
            var failed = Snapshot(); failed.Calendars[0].ReadSucceeded = false;
            var partial = clientB.Accept(failed, now.AddMinutes(20));
            check("watch_partial_calendar_failure_does_not_claim_cancellations", partial.Total == 0 && partial.Entries.Count == 0 && clientB.Accept(Snapshot(Event("two", "Synthetic new item", now.AddDays(2))), now.AddMinutes(25)).Total == 0);
            var backoff = new CalendarPollBackoff(); backoff.Failed(now);
            check("watch_backoff_starts_at_five_minutes", !backoff.Ready(now.AddMinutes(4)) && backoff.Ready(now.AddMinutes(5)));
            backoff.Failed(now.AddMinutes(5));
            check("watch_backoff_increases_after_repeated_failure", !backoff.Ready(now.AddMinutes(14)) && backoff.Ready(now.AddMinutes(15)));
            backoff.Succeeded(now.AddMinutes(15));
            check("watch_success_resets_five_minute_cadence", backoff.Failures == 0 && backoff.Ready(now.AddMinutes(20)));

            var privateProperties = new Dictionary<string, string> { { "cw.status.version", "1" } };
            foreach (var key in GoogleOnlyStatus.Keys) privateProperties["cw.status." + key] = key == "setup" ? "server status" : "";
            var remote = new RemoteEvent { Id = "synthetic-event", Summary = "Synthetic mapped item", Start = now.AddDays(1), End = now.AddDays(1).AddHours(1), PrivateProperties = privateProperties };
            string issue, binding;
            var read = RemoteStatusProjection.Read(remote, out issue, out binding);
            check("watch_server_private_status_is_readable_without_local_workbook", read["setup"] == "server status" && issue == null);
            remote.Description = StatusPayloadPolicy.BeginMarker + "\ncorrupt\n" + StatusPayloadPolicy.EndMarker;
            read = RemoteStatusProjection.Read(remote, out issue, out binding);
            check("watch_conflicting_description_does_not_replace_private_status", read["setup"] == "server status" && !String.IsNullOrEmpty(issue));
            remote.Description = "";
            var statusBinding = StatusVerification.Binding();
            var source = new SourceEvent { ExplicitId = "synthetic-source", Phase = "OPEN", Alias = "synthetic-alias", StatusBinding = statusBinding,
                Fields = new Dictionary<string, string> { { "setup", "local status" }, { "check", "local check" }, { "alphaTester", "local tester" }, { "realTester", "local tester" } } };
            var repo = new SyncRepository(Path.Combine(output, "watch-projection-" + Guid.NewGuid().ToString("N") + ".json"));
            repo.State.Mappings.Add(new EventMapping { Scope = "synthetic-scope", StableId = "e" + SyncHash.Text(source.ExplicitId + "|" + source.Phase), GoogleEventId = remote.Id, LastHash = remote.Hash });
            var book = new WorkbookEvents { DocumentId = statusBinding.DocumentId, Events = new List<SourceEvent> { source } };
            var projected = ConnectedCalendarSource.Project(book, repo, "synthetic-scope", "Synthetic shared calendar", "#9569D5", new List<RemoteEvent> { remote }, now,
                "synthetic-account", "synthetic-calendar", "synthetic@example.invalid");
            check("watch_excel_conflict_shows_server_and_blocks_local_edit", projected.Events[0].Fields["setup"] == "server status" &&
                !String.IsNullOrEmpty(projected.Events[0].StatusConflict) && projected.Events[0].StatusContext.ReadOnly && source.Fields["setup"] == "local status");
            var alertConfig = new WidgetConfiguration { ChangeAlertsEnabled = false, ChangeAlertsPausedUntilUtc = now.AddHours(1).ToString("o") };
            var alertPath = Path.Combine(output, "watch-settings-roundtrip.json");
            ConfigurationStore.Save(alertPath, alertConfig);
            var restoredAlerts = ConfigurationStore.Load(alertPath);
            check("watch_alert_preferences_roundtrip", !restoredAlerts.ChangeAlertsEnabled && restoredAlerts.ChangeAlertsPausedUntilUtc == alertConfig.ChangeAlertsPausedUntilUtc);
        }
        private static Button DayButton(DependencyObject root)
        {
            if (root == null) return null;
            var button = root as Button;
            if (button != null && button.Name == "DayButton") return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
                var found = DayButton(VisualTreeHelper.GetChild(root, i)); if (found != null) return found;
            }
            return null;
        }
        public static void RunUi(Widget widget, string output, Action<string, bool> check, Action<Window, string, double> capture)
        {
            var originalSource = widget.Source;
            var selected = widget.Configuration.SelectedProfileId;
            var visible = widget.Configuration.VisibleCalendarKeys == null ? null : widget.Configuration.VisibleCalendarKeys.ToList();
            bool enabled = widget.Configuration.ChangeAlertsEnabled;
            string paused = widget.Configuration.ChangeAlertsPausedUntilUtc;
            var now = widget.Now;
            var date = CalendarRules.InSeoul(now).Date.AddDays(1);
            var start = new DateTimeOffset(date.AddHours(11), TimeSpan.FromHours(9));
            var first = Event("ui-one", "Synthetic first", start);
            widget.Configuration.VisibleCalendarKeys = null;
            widget.Configuration.ChangeAlertsEnabled = true;
            widget.Configuration.ChangeAlertsPausedUntilUtc = null;
            widget.AcceptConnectedSnapshot(Snapshot(first));
            check("watch_ui_first_success_has_time_without_alert", widget.Find<Border>("ChangeBanner").Visibility == Visibility.Collapsed && widget.Find<TextBlock>("LastSuccessText").Text.Contains("2026.10.02"));
            var days = widget.Find<ItemsControl>("MonthDays");
            days.UpdateLayout();
            var day = days.Items.OfType<MonthDay>().Single(d => d.Date == date);
            var button = DayButton(days.ItemContainerGenerator.ContainerFromItem(day));
            widget.Window.Activate(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var popup = widget.ActiveDayPopup;
            check("watch_open_day_remains_until_user_closes_it", popup != null && popup.Window.IsVisible);
            widget.AcceptConnectedSnapshot(Snapshot(Event("ui-one", "Synthetic changed", start)));
            check("watch_poll_preserves_open_popup_and_defers_widget_replace", popup.Window.IsVisible && widget.Snapshot.Events[0].Title == "Synthetic first");
            popup.Find<Button>("PopupCloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("watch_deferred_change_applies_after_popup_close", widget.ActiveDayPopup == null && widget.Snapshot.Events[0].Title == "Synthetic changed" && widget.Find<Border>("ChangeBanner").Visibility == Visibility.Visible);
            check("watch_notification_summary_omits_private_title", widget.Find<TextBlock>("ChangeSummary").Text.Contains("수정 1") && !widget.Find<TextBlock>("ChangeSummary").Text.Contains("Synthetic changed"));
            capture(widget.Window, Path.Combine(output, "watch-change-banner-100.png"), 96);
            widget.Find<Button>("OpenChangeDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var detailPopup = widget.ActiveChangePopup;
            check("watch_alert_click_opens_change_details", detailPopup != null && detailPopup.Window.IsVisible && detailPopup.ChangeList.Items.Count == 1 &&
                detailPopup.DetailHeading.Text.Contains("Synthetic changed") && detailPopup.DetailRows.Children.Count > 0 &&
                detailPopup.Metadata.Text.Contains("확인 불가") && detailPopup.Metadata.Text.Contains("감지 시각"));
            capture(detailPopup.Window, Path.Combine(output, "watch-change-details-100.png"), 96);
            widget.AcceptConnectedSnapshot(Snapshot(Event("ui-one", "Synthetic changed", start)));
            check("watch_change_popup_defers_snapshot", detailPopup.Window.IsVisible && widget.ActiveChangePopup == detailPopup);
            detailPopup.Window.Close();
            check("watch_change_popup_closes_and_applies_deferred", widget.ActiveChangePopup == null && widget.Snapshot.Events[0].Title == "Synthetic changed");
            var statusAfter = Event("ui-one", "Synthetic first", start, new Dictionary<string, string> { { "setup", "ready" }, { "alphaCheck", "reviewed" } });
            var statusEntry = CalendarChangeDetail.Create(Snapshot(first).Calendars[0], first, statusAfter, now, false);
            var removalEntry = CalendarChangeDetail.Create(Snapshot(first).Calendars[0], first, null, now, false);
            var fieldPopup = new CalendarChangePopup(widget.Window, new[] { statusEntry, removalEntry }); fieldPopup.Window.Show();
            check("watch_status_change_popup_renders_before_after", fieldPopup.DetailRows.Children.OfType<TextBlock>().Any(t => t.Text.Contains("세팅") && t.Text.Contains("ready")));
            capture(fieldPopup.Window, Path.Combine(output, "watch-status-details-150.png"), 144);
            fieldPopup.ChangeList.SelectedIndex = 1;
            check("watch_removed_item_does_not_claim_proven_cancellation", fieldPopup.Note.Text.Contains("구별할 수 없습니다") && fieldPopup.DetailHeading.Text.Contains("취소"));
            fieldPopup.Window.Close();
            widget.Find<Button>("DismissChangeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            widget.Configuration.ChangeAlertsEnabled = false;
            widget.AcceptConnectedSnapshot(Snapshot(Event("ui-one", "Synthetic changed again", start)));
            check("watch_disabled_alert_still_updates_calendar_without_banner", widget.Snapshot.Events[0].Title == "Synthetic changed again" && widget.Find<Border>("ChangeBanner").Visibility == Visibility.Collapsed);
            widget.Configuration.ChangeAlertsEnabled = true;
            widget.Configuration.ChangeAlertsPausedUntilUtc = now.AddHours(1).ToString("o");
            widget.AcceptConnectedSnapshot(Snapshot(Event("ui-one", "Synthetic changed once more", start)));
            check("watch_paused_alert_still_updates_calendar_without_banner", widget.Snapshot.Events[0].Title == "Synthetic changed once more" && widget.Find<Border>("ChangeBanner").Visibility == Visibility.Collapsed);
            var beforeSettings = JsonData.Serialize(widget.Configuration);
            var settings = new SettingsDialog(widget); settings.Window.Show();
            settings.Find<TabControl>("ConfigTabs").SelectedIndex = 4;
            settings.Window.UpdateLayout();
            settings.Window.Dispatcher.Invoke(new Action(delegate { settings.Window.UpdateLayout(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            settings.Find<CheckBox>("ChangeAlertsEnabled").IsChecked = false;
            settings.Find<Button>("PauseChangeAlertsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("watch_settings_toggle_and_pause_are_visible", settings.Find<CheckBox>("ChangeAlertsEnabled").IsVisible && settings.Find<Button>("PauseChangeAlertsButton").IsVisible);
            ((ScrollViewer)((TabItem)settings.Find<TabControl>("ConfigTabs").Items[4]).Content).ScrollToBottom();
            settings.Window.UpdateLayout();
            capture(settings.Window, Path.Combine(output, "watch-settings-100.png"), 96);
            settings.Find<Button>("CancelSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("watch_settings_cancel_preserves_alert_preferences", JsonData.Serialize(widget.Configuration) == beforeSettings);
            widget.Configuration.ChangeAlertsEnabled = enabled; widget.Configuration.ChangeAlertsPausedUntilUtc = paused;
            widget.Configuration.VisibleCalendarKeys = visible; widget.Configuration.SelectedProfileId = selected;
            widget.Source = originalSource; widget.Reload();
            widget.Find<Button>("TodayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
