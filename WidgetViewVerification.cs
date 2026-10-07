using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CalendarWidget
{
    public static class WidgetViewVerification
    {
        private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        public static void RunUi(Widget widget, string output, Action<string, bool> check, Action<Window, string, double> capture)
        {
            var config = widget.Configuration;
            string oldSearch = config.SearchText, oldFilter = config.QaFilter, oldDensity = config.ViewDensity, oldSelected = config.SelectedProfileId;
            var oldVisible = config.VisibleCalendarKeys == null ? null : config.VisibleCalendarKeys.ToList();
            var oldPresets = config.ViewPresets.ToList();
            double oldWidth = widget.Window.Width, oldHeight = widget.Window.Height;
            Click(widget.Find<Button>("FilterButton"));
            var search = widget.Find<TextBox>("SearchBox");
            string title = widget.Snapshot.Events.First().Title;
            search.Text = title.Substring(0, Math.Min(3, title.Length));
            check("view_search_reveals_title_results_without_initial_agenda", widget.Find<StackPanel>("DetailHost").Visibility == Visibility.Visible &&
                widget.Find<ItemsControl>("EventList").Items.OfType<EventView>().Any(v => v.Title == title));
            capture(widget.Window, Path.Combine(output, "view-search-100.png"), 96);
            widget.Find<ComboBox>("QaFilterSelect").SelectedIndex = 1;
            check("view_qa_filter_is_separate_from_sync_summary", config.QaFilter == "recorded" && widget.Find<TextBlock>("QaSummaryText").Text.StartsWith("QA 상태") &&
                widget.Find<TextBlock>("SyncSummaryText").Text.StartsWith("동기화"));
            widget.Find<ComboBox>("QaFilterSelect").SelectedIndex = 0;
            var density = widget.Find<ComboBox>("DensitySelect");
            density.SelectedIndex = 1;
            double compact = widget.Find<ItemsControl>("MonthDays").Items.OfType<MonthDay>().First().DayHeight;
            density.SelectedIndex = 0;
            double comfortable = widget.Find<ItemsControl>("MonthDays").Items.OfType<MonthDay>().First().DayHeight;
            check("view_density_changes_day_height_and_overflow_is_explicit", compact < comfortable &&
                MonthCalendar.Build(widget.Snapshot, null, new DateTime(2026, 10, 1), new DateTime(2026, 10, 2), null).All(d => d.EventCount <= 2 || d.CountLabel.StartsWith("+")));
            density.SelectedIndex = 1;
            config.VisibleCalendarKeys = new List<string> { widget.Snapshot.Calendars.First().Id }; widget.Render();
            widget.Find<TextBox>("PresetName").Text = "Synthetic view";
            Click(widget.Find<Button>("SavePresetButton"));
            check("view_preset_captures_filter_density_and_size", config.ViewPresets.Count == oldPresets.Count + 1 &&
                config.ViewPresets.Last().SearchText == search.Text && config.ViewPresets.Last().ViewDensity == "compact" &&
                config.ViewPresets.Last().Width == widget.Window.Width && config.ViewPresets.Last().VisibleCalendarKeys.Count == 1);
            search.Text = "no synthetic match"; density.SelectedIndex = 0;
            config.VisibleCalendarKeys = null;
            Click(widget.Find<Button>("ApplyPresetButton"));
            check("view_preset_restores_search_calendar_and_density", search.Text == title.Substring(0, Math.Min(3, title.Length)) && config.ViewDensity == "compact" &&
                config.VisibleCalendarKeys != null && config.VisibleCalendarKeys.Count == 1);
            Click(widget.Find<Button>("ConnectionDiagnosticsButton"));
            var diagnostic = widget.Find<TextBlock>("ConnectionDiagnosticsText").Text;
            check("view_diagnostic_excludes_email_token_and_event_body", diagnostic.Contains("연결 진단") && !diagnostic.Contains("@") && !diagnostic.Contains(title) && !diagnostic.Contains("token"));
            capture(widget.Window, Path.Combine(output, "view-diagnostics-100.png"), 96);
            widget.Find<DatePicker>("JumpDate").SelectedDate = new DateTime(2026, 10, 2);
            Click(widget.Find<Button>("JumpDateButton"));
            check("view_date_jump_opens_existing_day_popup", widget.ActiveDayPopup != null && widget.ActiveDayPopup.Date == new DateTime(2026, 10, 2));
            widget.ActiveDayPopup.Window.Close();
            config.SearchText = oldSearch; config.QaFilter = oldFilter; config.ViewDensity = oldDensity;
            config.SelectedProfileId = oldSelected; config.VisibleCalendarKeys = oldVisible; config.ViewPresets = oldPresets;
            search.Text = oldSearch; widget.Find<ComboBox>("QaFilterSelect").SelectedIndex = 0;
            widget.Find<ComboBox>("DensitySelect").SelectedIndex = oldDensity == "compact" ? 1 : 0;
            widget.Window.Width = oldWidth; widget.Window.Height = oldHeight;
            Click(widget.Find<Button>("TodayButton"));
            widget.Find<Border>("FilterPane").Visibility = Visibility.Collapsed;
            widget.Render();
        }
    }
}
