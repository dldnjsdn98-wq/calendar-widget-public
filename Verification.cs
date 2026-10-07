using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CalendarWidget
{
    public static class Verification
    {
        private static readonly List<object> checks = new List<object>();
        private static void Check(string name, bool passed)
        {
            checks.Add(new { name = name, passed = passed });
            if (!passed) throw new Exception("검증 실패: " + name);
        }
        private static bool Rejects(Action action) { try { action(); return false; } catch { return true; } }
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static T Descendant<T>(System.Windows.DependencyObject node) where T : System.Windows.DependencyObject
        {
            if (node == null) return null;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is T) return (T)child;
                var found = Descendant<T>(child); if (found != null) return found;
            }
            return null;
        }
        private static void Drain(Window window) { window.Dispatcher.Invoke(new Action(delegate { window.UpdateLayout(); }), DispatcherPriority.ApplicationIdle); }
        public static int Run(string root, string output)
        {
            Directory.CreateDirectory(output);
            int result = 1;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Widget widget = null;
            try
            {
                Check("os_default_tls_enabled", System.Net.ServicePointManager.SecurityProtocol == (System.Net.SecurityProtocolType)0);
                var source = new LocalSampleSource(Path.Combine(root, "sample-data.json"));
                var data = source.Load();
                var now = DateTimeOffset.Parse("2026-10-02T15:00:00+09:00", CultureInfo.InvariantCulture);
                Check("sample_mode_and_profiles", data.Mode == "sample" && data.Calendars.Count == 2 && data.Events.Count == 4);
                Check("brownfarm_extra_fields", data.Calendars[0].Fields.Select(f => f.Label).SequenceEqual(new[] { "세팅", "확인", "알파와 테스터", "리얼과 테스터" }));
                Check("calendar_filter", CalendarRules.Project(data, "brownfarm", now).Count == 3 && CalendarRules.Project(data, "personal", now).Count == 1);
                var festival = CalendarRules.Project(data, null, now).First(ev => ev.Title == "농장 축제");
                Check("all_day_end_exclusive", festival.Schedule.Contains("10.11") && !festival.Schedule.Contains("10.12"));
                Check("missing_timezone_rejected", Rejects(delegate { CalendarRules.ParseDate("2026-10-02T11:00:00", false); }));
                Check("seoul_day_rollover", CalendarRules.Cutoff(DateTimeOffset.Parse("2026-10-02T15:00:00Z")) == new DateTime(2026, 10, 3));
                Check("minimum_date", CalendarRules.Cutoff(DateTimeOffset.Parse("2026-10-01T00:00:00Z")) == new DateTime(2026, 10, 2));
                var copy = new JavaScriptSerializer().Deserialize<CalendarSnapshot>(new JavaScriptSerializer().Serialize(data));
                copy.Events.Add(new CalendarEvent { Id = "boundary", CalendarId = "personal", Title = "자정 종료", Start = "2026-10-01T23:00:00+09:00", End = "2026-10-02T00:00:00+09:00", Fields = new Dictionary<string, string>() });
                Check("midnight_end_excluded", CalendarRules.Project(copy, null, now).Count == 4);
                Check("empty_after_sample_dates", CalendarRules.Project(data, null, DateTimeOffset.Parse("2026-11-01T00:00:00+09:00")).Count == 0);
                var month = MonthCalendar.Build(data, null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), null);
                Check("month_six_week_boundary_and_today", month.Count == 42 && month[0].Date == new DateTime(2026, 9, 27) && month[41].Date == new DateTime(2026, 11, 7) && month.Single(d => d.IsToday).Date.Day == 2);
                Check("month_navigation_rolls_year", MonthCalendar.Move(new DateTime(2026, 12, 1), 1) == new DateTime(2027, 1, 1) && MonthCalendar.Move(new DateTime(2026, 1, 1), -1) == new DateTime(2025, 12, 1));
                var overlap = new CalendarEvent { Start = "2026-10-02T23:30:00+09:00", End = "2026-10-03T00:30:00+09:00", IsPoint = false };
                Check("month_timed_midnight_overlap_and_exclusive_end", MonthCalendar.Occurs(overlap, new DateTime(2026, 10, 2)) && MonthCalendar.Occurs(overlap, new DateTime(2026, 10, 3)) && !MonthCalendar.Occurs(overlap, new DateTime(2026, 10, 4)));
                overlap.AllDay = true; overlap.Start = "2026-10-02"; overlap.End = "2026-10-04";
                Check("month_all_day_end_exclusive", MonthCalendar.Occurs(overlap, new DateTime(2026, 10, 3)) && !MonthCalendar.Occurs(overlap, new DateTime(2026, 10, 4)));
                Check("month_filter_matches_detail_filter", MonthCalendar.Build(data, "personal", new DateTime(2026, 10, 1), now.Date, null).Sum(d => d.Marks.Count) <= month.Sum(d => d.Marks.Count));
                copy.Events[0].End = copy.Events[0].Start;
                Check("invalid_duration_rejected", Rejects(delegate { CalendarRules.Validate(copy); }));
                var google = new GoogleCalendarProvider();
                var account = new AccountConfiguration { Id = "test", Email = "test@example.invalid", ProviderId = "google", DisplayName = "검증용", CalendarIds = new List<string>() };
                Check("email_does_not_authenticate", google.ConnectionState(account).StartsWith("미연결") && Rejects(delegate { google.CreateSource(account); }));
                var payload = GooglePayloadPolicy.Build("테스트 일정", now, now.AddMinutes(1), "3");
                Check("google_payload_excludes_meet_attendees_description", !payload.ContainsKey("conferenceData") && !payload.ContainsKey("hangoutLink") && !payload.ContainsKey("attendees") && !payload.ContainsKey("description"));
                Check("google_response_clean", !GooglePayloadPolicy.ResponseNeedsReview(payload));
                Check("google_response_meet_requires_review", GooglePayloadPolicy.ResponseNeedsReview(new Dictionary<string, object> { { "hangoutLink", "unexpected" } }) && GooglePayloadPolicy.ResponseNeedsReview(new Dictionary<string, object> { { "conferenceData", new object() } }));
                Check("google_response_attendees_or_description_requires_review", GooglePayloadPolicy.ResponseNeedsReview(new Dictionary<string, object> { { "attendees", new object[] { new object() } } }) && GooglePayloadPolicy.ResponseNeedsReview(new Dictionary<string, object> { { "description", "unexpected source text" } }));
                var settings = new WidgetConfiguration(); settings.Accounts.Add(account); settings.Accounts.Add(new AccountConfiguration { Id = "test2", ProviderId = "google", CalendarIds = new List<string> { "calendar-example" } });
                settings.VisibleFields["brownfarm"] = new List<string> { "check" };
                var configFile = Path.Combine(output, "config-roundtrip.json");
                ConfigurationStore.Save(configFile, settings); ConfigurationStore.Save(configFile, settings);
                var loaded = ConfigurationStore.Load(configFile);
                Check("multiple_accounts_and_calendar_selection_roundtrip", loaded.Accounts.Count == 2 && loaded.Accounts[1].CalendarIds[0] == "calendar-example" && loaded.VisibleFields["brownfarm"].Count == 1);
                var invalidAppearance = new WidgetConfiguration { ThemeMode = "invalid", FontFamilyName = "missing-font-that-is-not-installed", DisplayFontSize = 99 };
                ThemeManager.Normalize(invalidAppearance);
                Check("appearance_rejects_missing_font_and_out_of_range_size", invalidAppearance.ThemeMode == "light" && invalidAppearance.FontFamilyName == ThemeManager.DefaultFont && invalidAppearance.DisplayFontSize == ThemeManager.DefaultSize);
                var systemAppearance = new WidgetConfiguration { ThemeMode = "system" }; ThemeManager.Normalize(systemAppearance);
                Check("appearance_system_mode_is_supported", systemAppearance.ThemeMode == "system");
                Check("no_secrets_in_settings", !File.ReadAllText(configFile).ToLowerInvariant().Contains("token") && !File.ReadAllText(configFile).ToLowerInvariant().Contains("secret"));
                File.WriteAllText(Path.Combine(output, "malformed.json"), "{broken");
                Check("malformed_json_rejected", Rejects(delegate { new LocalSampleSource(Path.Combine(output, "malformed.json")).Load(); }));
                Check("missing_data_rejected", Rejects(delegate { new LocalSampleSource(Path.Combine(output, "absent.json")).Load(); }));
                AdvancedVerification.Run(output, Check);
                CalendarChangeWatchVerification.Run(output, Check);
                UpdateVerification.Run(root, output, Check);
                SortedWorkbookVerification.Run(output, Check);
                ConnectedCalendarVerification.Run(output, Check);
                StatusVerification.Run(output, Check);
                StatusEndpointVerification.Run(output, Check);
                GoogleOnlyStatusVerification.Run(output, Check);
                ManualEventVerification.Run(output, Check);
                widget = new Widget(root, true) { FixedNow = now };
                widget.Reload();
                app.MainWindow = widget.Window;
                widget.Window.ContentRendered += delegate
                {
                    app.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            var list = widget.Find<ItemsControl>("EventList");
                            Check("native_widget_window", widget.Window.WindowStyle == WindowStyle.None && !widget.Window.ShowInTaskbar && !widget.Window.Topmost);
                            Check("initial_brownfarm_selection", list.Items.Count == 3 && (string)widget.Find<ComboBox>("CalendarSelect").SelectedValue == "brownfarm");
                            Check("timezone_visible", widget.Find<TextBlock>("ClockText").Text.Contains("Asia/Seoul"));
                            var days = widget.Find<ItemsControl>("MonthDays");
                            Check("month_grid_renders_42_dates", days.Items.Count == 42 && widget.Find<TextBlock>("MonthTitle").Text.Contains("2026년 10월"));
                            Check("initial_view_shows_calendar_without_detail_or_filter", widget.Find<StackPanel>("DetailHost").Visibility == Visibility.Collapsed && widget.Find<Border>("FilterPane").Visibility == Visibility.Collapsed && !widget.Find<TextBlock>("ModeTitle").IsVisible);
                            Capture(widget.Window, Path.Combine(output, "calendar-compact-100.png"), 96);
                            Capture(widget.Window, Path.Combine(output, "calendar-compact-125.png"), 120);
                            Capture(widget.Window, Path.Combine(output, "calendar-compact-150.png"), 144);
                            Capture(widget.Window, Path.Combine(output, "calendar-compact-200.png"), 192);
                            widget.Window.Width = 320; Drain(widget.Window); Capture(widget.Window, Path.Combine(output, "calendar-minwidth-320.png"), 96);
                            widget.Window.Width = 360; Drain(widget.Window);
                            var todayDay = days.Items.OfType<MonthDay>().Single(d => d.Date == new DateTime(2026, 10, 2));
                            var container = days.ItemContainerGenerator.ContainerFromItem(todayDay) as System.Windows.Controls.ContentPresenter;
                            var dayButton = Descendant<Button>(container);
                            Check("month_day_button_exists", dayButton != null && dayButton.Name == "DayButton");
                            Click(dayButton); Drain(widget.Window);
                            Check("month_click_filters_detail_list", widget.SelectedDate == todayDay.Date && list.Items.Count == CalendarRules.ProjectDate(data, "brownfarm", todayDay.Date, now).Count);
                            var dayPopup = widget.ActiveDayPopup;
                            Check("date_click_opens_one_popup_without_auto_selecting_event", dayPopup != null && dayPopup.Window.IsVisible && dayPopup.AgendaList.Items.Count == list.Items.Count && dayPopup.SelectedEvent == null);
                            Capture(dayPopup.Window, Path.Combine(output, "widget-day-popup.png"), 96);
                            dayPopup.AgendaList.SelectedIndex = 0;
                            Check("event_selection_reveals_detail_in_popup", dayPopup.SelectedEvent != null && dayPopup.Find<Border>("EventDetail").Visibility == Visibility.Visible);
                            Capture(dayPopup.Window, Path.Combine(output, "widget-day-detail.png"), 144);
                            Click(dayPopup.Find<Button>("PopupCloseButton")); Drain(widget.Window);
                            Check("popup_close_returns_focus_and_clears_instance", widget.ActiveDayPopup == null && widget.Window.IsVisible);
                            var emptyDay = days.Items.OfType<MonthDay>().Single(d => d.Date == new DateTime(2026, 10, 20));
                            var emptyButton = Descendant<Button>(days.ItemContainerGenerator.ContainerFromItem(emptyDay) as ContentPresenter);
                            widget.Window.Activate(); Drain(widget.Window);
                            Click(emptyButton); Drain(widget.Window);
                            Check("empty_date_opens_empty_popup_" + (widget.ActiveDayPopup == null ? "closed_" + widget.LastPopupCloseReason : "open_" + widget.ActiveDayPopup.AgendaList.Items.Count + "_" + widget.ActiveDayPopup.Find<TextBlock>("EmptyDayText").Visibility), widget.ActiveDayPopup != null && widget.ActiveDayPopup.AgendaList.Items.Count == 0 && widget.ActiveDayPopup.Find<TextBlock>("EmptyDayText").Visibility == Visibility.Visible);
                            var emptyPopup = widget.ActiveDayPopup; Click(emptyPopup.Find<Button>("PopupCloseButton"));
                            Check("empty_popup_closes_without_duplicate", widget.ActiveDayPopup == null);
                            Click(widget.Find<Button>("NextMonthButton")); Check("month_next_clears_day_and_moves", widget.SelectedDate == null && widget.Find<TextBlock>("MonthTitle").Text.Contains("11월"));
                            Click(widget.Find<Button>("PreviousMonthButton")); Check("month_previous_restores_initial_list", widget.SelectedDate == null && widget.Find<TextBlock>("MonthTitle").Text.Contains("10월") && list.Items.Count == 3);
                            Capture(widget.Window, Path.Combine(output, "widget-brownfarm.png"), 96);
                            Capture(widget.Window, Path.Combine(output, "widget-brownfarm-150pct.png"), 144);
                            widget.Find<ComboBox>("CalendarSelect").SelectedValue = "personal";
                            Drain(widget.Window);
                            checks.Add(new { name = "personal_selection_observation", selected = widget.Find<ComboBox>("CalendarSelect").SelectedValue, count = list.Items.Count });
                            Check("calendar_selection_updates_ui", list.Items.Count == 1 && ((EventView)list.Items[0]).Fields.Count == 1);
                            Capture(widget.Window, Path.Combine(output, "widget-personal.png"), 96);
                            widget.Find<ComboBox>("CalendarSelect").SelectedValue = "";
                            Drain(widget.Window);
                            Check("all_calendars_ui", list.Items.Count == 4);
                            Click(widget.Find<Button>("PinButton")); Check("pin_on", widget.Window.Topmost);
                            Click(widget.Find<Button>("PinButton")); Check("pin_off", !widget.Window.Topmost);
                            widget.Find<ComboBox>("CalendarSelect").SelectedValue = "brownfarm";
                            Drain(widget.Window);
                            var dialog = new SettingsDialog(widget);
                            dialog.Window.Show();
                            Check("sync_disabled_until_real_connection", !dialog.Find<Button>("SyncButton").IsEnabled);
                            Click(dialog.Find<Button>("AddAccountButton"));
                            dialog.Find<TextBox>("AccountName").Text = "업무 계정 (샘플 입력)";
                            dialog.Find<TextBox>("AccountEmail").Text = "";
                            Capture(dialog.Window, Path.Combine(output, "settings-disconnected.png"), 96);
                            dialog.Find<TabControl>("ConfigTabs").SelectedIndex = 1;
                            var settingsScroll = dialog.Find<ScrollViewer>("SourceScroll");
                            settingsScroll.ScrollToBottom(); Drain(dialog.Window);
                            Capture(dialog.Window, Path.Combine(output, "settings-sync-disabled.png"), 96);
                            var boxes = dialog.Find<StackPanel>("FieldChoices").Children.OfType<CheckBox>().ToList();
                            Check("per_calendar_field_choices", boxes.Count == 4);
                            boxes[0].IsChecked = false;
                            Click(dialog.Find<Button>("SaveSettingsButton"));
                            Check("account_saved_without_connection", widget.Configuration.Accounts.Count == 1 && widget.Configuration.Accounts[0].ProviderId == "google");
                            Check("field_visibility_updates_ui", ((EventView)list.Items[0]).Fields.Count == 3);
                            UiWorkflowVerification.Run(widget, output, Check, Capture);
                            string previousTheme = widget.Configuration.ThemeMode, previousFont = widget.Configuration.FontFamilyName;
                            double previousSize = widget.Configuration.DisplayFontSize;
                            var appearanceCancel = new SettingsDialog(widget); appearanceCancel.Window.Show();
                            appearanceCancel.Find<TabControl>("ConfigTabs").SelectedIndex = 4;
                            appearanceCancel.Find<ComboBox>("ThemeSelect").SelectedIndex = 1;
                            appearanceCancel.Find<Slider>("FontSizeSelect").Value = 18;
                            appearanceCancel.Find<ComboBox>("ThemeSelect").IsDropDownOpen = true; Drain(appearanceCancel.Window);
                            Check("dark_theme_dropdown_opens_with_items", appearanceCancel.Find<ComboBox>("ThemeSelect").IsDropDownOpen && appearanceCancel.Find<ComboBox>("ThemeSelect").Items.Count == 3);
                            var themePopup = appearanceCancel.Find<ComboBox>("ThemeSelect").Template.FindName("PART_Popup", appearanceCancel.Find<ComboBox>("ThemeSelect")) as System.Windows.Controls.Primitives.Popup;
                            Check("dark_theme_dropdown_popup_has_surface", themePopup != null && themePopup.IsOpen && themePopup.Child != null);
                            if (themePopup != null && themePopup.Child is FrameworkElement) CaptureElement((FrameworkElement)themePopup.Child, ThemeManager.Get(appearanceCancel.Window, "ThemeSurface"), Path.Combine(output, "appearance-dark-dropdown-100.png"), 96);
                            appearanceCancel.Find<ComboBox>("ThemeSelect").IsDropDownOpen = false;
                            Capture(appearanceCancel.Window, Path.Combine(output, "appearance-dark-preview-100.png"), 96);
                            Capture(appearanceCancel.Window, Path.Combine(output, "appearance-dark-preview-200.png"), 192);
                            Check("appearance_preview_dark_and_large", appearanceCancel.Window.FontSize == 18 && ThemeManager.Get(appearanceCancel.Window, "ThemeSurface").ToString() == "#FF24222B");
                            Click(appearanceCancel.Find<Button>("CancelSettingsButton"));
                            Check("appearance_cancel_restores_widget", widget.Configuration.ThemeMode == previousTheme && widget.Configuration.DisplayFontSize == previousSize && widget.Window.FontSize == previousSize);
                            var appearanceSave = new SettingsDialog(widget); appearanceSave.Window.Show();
                            appearanceSave.Find<TabControl>("ConfigTabs").SelectedIndex = 4;
                            appearanceSave.Find<ComboBox>("ThemeSelect").SelectedIndex = 1;
                            appearanceSave.Find<ComboBox>("FontSelect").SelectedItem = ThemeManager.InstalledFonts().Contains("Segoe UI") ? "Segoe UI" : ThemeManager.DefaultFont;
                            appearanceSave.Find<Slider>("FontSizeSelect").Value = 18;
                            Click(appearanceSave.Find<Button>("SaveSettingsButton")); Drain(widget.Window);
                            Check("appearance_save_applies_to_widget", widget.Configuration.ThemeMode == "dark" && widget.Configuration.DisplayFontSize == 18 && widget.Window.FontSize == 18 && ThemeManager.IsDark(widget.Configuration));
                            var appearancePath = Path.Combine(output, "appearance-roundtrip.json"); ConfigurationStore.Save(appearancePath, widget.Configuration);
                            var restoredAppearance = ConfigurationStore.Load(appearancePath);
                            Check("appearance_persists_and_restarts", restoredAppearance.ThemeMode == "dark" && restoredAppearance.DisplayFontSize == 18 && restoredAppearance.FontFamilyName == widget.Configuration.FontFamilyName);
                            widget.Render(); Capture(widget.Window, Path.Combine(output, "calendar-dark-large-100.png"), 96);
                            File.WriteAllText(Path.Combine(output, "appearance-brush-audit.txt"), "filter-bg=" + widget.Find<Button>("FilterButton").Background + ";filter-fg=" + widget.Find<Button>("FilterButton").Foreground + ";tint=" + ThemeManager.Get(widget.Window, "ThemeSurfaceTint"));
                            Check("dark_widget_header_controls_have_contrast", widget.Find<Button>("FilterButton").Background.ToString() != "#FFFFFFFF" && widget.Find<Button>("FilterButton").Foreground.ToString() != widget.Find<Button>("FilterButton").Background.ToString());
                            Capture(widget.Window, Path.Combine(output, "calendar-dark-large-125.png"), 120);
                            Capture(widget.Window, Path.Combine(output, "calendar-dark-large-150.png"), 144);
                            Capture(widget.Window, Path.Combine(output, "calendar-dark-large-200.png"), 192);
                            widget.Window.Width = 320; Drain(widget.Window); Capture(widget.Window, Path.Combine(output, "calendar-dark-large-minwidth-320.png"), 96);
                            widget.Window.Width = 360; Drain(widget.Window);
                            var darkPopup = new DayAgendaPopup(widget.Window, root, new DateTime(2026, 10, 2), new List<EventView>(), v => { }); darkPopup.Window.Show();
                            Check("popup_inherits_dark_large_appearance", darkPopup.Window.FontSize == 18 && ThemeManager.Get(darkPopup.Window, "ThemeSurface").ToString() == "#FF24222B");
                            Capture(darkPopup.Window, Path.Combine(output, "popup-dark-large-200.png"), 192); darkPopup.Window.Close();
                            var darkManual = new ManualEventEditor(widget.Window, root, new DateTime(2026, 10, 2), new List<ManualCalendarChoice>()); darkManual.Window.Show();
                            Check("manual_editor_dark_text_distinct_from_background", darkManual.Window.FontSize == 18 && !darkManual.Find<TextBox>("ManualTitle").Foreground.ToString().Equals(darkManual.Find<TextBox>("ManualTitle").Background.ToString(), StringComparison.OrdinalIgnoreCase));
                            Capture(darkManual.Window, Path.Combine(output, "manual-dark-large-200.png"), 192); darkManual.Window.Close();
                            StatusVerification.RunUi(widget, output, Check, Capture);
                            StatusHotfixVerification.RunUi(widget, output, Check, Capture);
                            CalendarChangeWatchVerification.RunUi(widget, output, Check, Capture);
                            UpdateVerification.RunUi(widget, output, Check, Capture);
                            WidgetViewVerification.RunUi(widget, output, Check, Capture);
                            widget.Configuration.ThemeMode = previousTheme; widget.Configuration.FontFamilyName = previousFont; widget.Configuration.DisplayFontSize = previousSize;
                            ThemeManager.Apply(widget.Window, widget.Configuration); widget.Render();
                            widget.Source = new LocalSampleSource(Path.Combine(output, "malformed.json")); widget.Reload();
                            Check("reload_failure_marks_previous_data_stale", widget.Find<TextBlock>("StatusText").Text.Contains("最新") == false && widget.Find<TextBlock>("StatusText").Text.Contains("최신 아님") && list.Items.Count == 3);
                            Capture(widget.Window, Path.Combine(output, "widget-data-error.png"), 96);
                            widget.Source = source; widget.Reload();
                            Check("reload_recovery", !widget.Find<TextBlock>("StatusText").Text.Contains("실패") && list.Items.Count == 3);
                            widget.FixedNow = DateTimeOffset.Parse("2026-11-01T00:00:00+09:00"); widget.Render();
                            Check("empty_state_ui", list.Items.Count == 0 && widget.Find<Border>("EmptyPanel").Visibility == Visibility.Visible);
                            Capture(widget.Window, Path.Combine(output, "widget-empty.png"), 96);
                            widget.FixedNow = now; widget.Configuration.VisibleFields.Clear(); widget.Reload();
                            widget.Window.Width = 350; widget.Window.Height = 380;
                            Check("minimum_size_has_scroll", widget.Find<ScrollViewer>("EventScroll").VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
                            Capture(widget.Window, Path.Combine(output, "widget-small.png"), 96);
                            result = 0;
                        }
                        catch (Exception ex) { checks.Add(new { name = "ui_exception", passed = false, detail = ex.ToString() }); }
                        finally { widget.Window.Close(); app.Shutdown(result); }
                    }), DispatcherPriority.ApplicationIdle);
                };
                widget.Window.Show();
                app.Run();
            }
            catch (Exception ex) { checks.Add(new { name = "verification_exception", passed = false, detail = ex.ToString() }); }
            finally
            {
                if (widget != null) widget.Dispose();
                File.WriteAllText(Path.Combine(output, "verification.json"), new JavaScriptSerializer().Serialize(new { passed = result == 0, checks = checks,
                    rendering = "WPF RenderTargetBitmap of shown app windows; not desktop screenshot or human pointer tests", fixedNow = "2026-10-02T15:00:00+09:00", testedAtUtc = DateTimeOffset.UtcNow.ToString("o") }));
            }
            return result;
        }
        public static void Capture(Window window, string path, double dpi)
        {
            window.UpdateLayout();
            var content = window.Content as FrameworkElement;
            content.Measure(new Size(window.ActualWidth, window.ActualHeight));
            content.Arrange(new Rect(new Point(0, 0), new Size(window.ActualWidth, window.ActualHeight)));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi / 96), (int)Math.Ceiling(content.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                drawing.DrawRectangle(window.Background, null, bounds);
                drawing.DrawRectangle(new VisualBrush(content), null, bounds);
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
        }
        public static void CaptureElement(FrameworkElement content, Brush background, string path, double dpi)
        {
            content.UpdateLayout();
            var width = Math.Max(1, content.ActualWidth);
            var height = Math.Max(1, content.ActualHeight);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 96), (int)Math.Ceiling(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) { var bounds = new Rect(0, 0, width, height); drawing.DrawRectangle(background, null, bounds); drawing.DrawRectangle(new VisualBrush(content), null, bounds); }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
        }
    }
}
