using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CalendarWidget
{
    public sealed partial class Widget
    {
        private bool applyingViewPreset;
        private static string Tag(ComboBox combo)
        {
            var item = combo.SelectedItem as ComboBoxItem;
            return item == null ? null : item.Tag as string;
        }
        private static void SelectTag(ComboBox combo, string tag)
        {
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag) ?? combo.Items[0];
        }
        private void InitializeViewFeatures()
        {
            var search = Find<TextBox>("SearchBox");
            var qa = Find<ComboBox>("QaFilterSelect");
            var density = Find<ComboBox>("DensitySelect");
            search.Text = Configuration.SearchText ?? "";
            SelectTag(qa, Configuration.QaFilter ?? "all");
            SelectTag(density, Configuration.ViewDensity ?? "comfortable");
            Find<ComboBox>("PresetSelect").ItemsSource = Configuration.ViewPresets;
            search.TextChanged += delegate { if (applyingViewPreset) return; Configuration.SearchText = search.Text; Render(); };
            search.KeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.Key != Key.Enter) return;
                Find<Border>("FilterPane").Visibility = Visibility.Collapsed;
                Find<ScrollViewer>("EventScroll").ScrollToTop();
                args.Handled = true;
            };
            search.LostFocus += delegate { Save(); };
            qa.SelectionChanged += delegate { if (applyingViewPreset) return; Configuration.QaFilter = Tag(qa) ?? "all"; Render(); Save(); };
            density.SelectionChanged += delegate { if (applyingViewPreset) return; Configuration.ViewDensity = Tag(density) ?? "comfortable"; Render(); Save(); };
            Find<Button>("JumpDateButton").Click += delegate { JumpToDate(); };
            Find<Button>("SavePresetButton").Click += delegate { SaveViewPreset(); };
            Find<Button>("ApplyPresetButton").Click += delegate { ApplyViewPreset(); };
            Find<Button>("DeletePresetButton").Click += delegate { DeleteViewPreset(); };
            Find<Button>("ConnectionDiagnosticsButton").Click += delegate { ShowSafeDiagnostics(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs args) {
                if (args.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) {
                    Find<Border>("FilterPane").Visibility = Visibility.Visible;
                    search.Focus(); search.SelectAll(); args.Handled = true;
                } else if (args.Key == Key.Escape && Find<Border>("FilterPane").Visibility == Visibility.Visible && ActiveDayPopup == null) {
                    Find<Border>("FilterPane").Visibility = Visibility.Collapsed; args.Handled = true;
                }
            };
        }
        private void JumpToDate()
        {
            var date = Find<DatePicker>("JumpDate").SelectedDate;
            if (!date.HasValue) { statusText.Text = "이동할 날짜를 먼저 선택하세요."; return; }
            displayedMonth = new DateTime(date.Value.Year, date.Value.Month, 1);
            SelectedDate = date.Value.Date;
            Find<Border>("FilterPane").Visibility = Visibility.Collapsed;
            Render();
            OpenDayPopup(date.Value.Date, DayAgendaPopup.AnchorPoint(Find<Button>("JumpDateButton")));
        }
        private void OpenSearchResult(EventView view, FrameworkElement anchor)
        {
            if (Snapshot == null) return;
            var item = Snapshot.Events.FirstOrDefault(e => e.Id == view.Id);
            if (item == null) return;
            var date = CalendarRules.InSeoul(CalendarRules.ParseDate(item.Start, item.AllDay)).Date;
            displayedMonth = new DateTime(date.Year, date.Month, 1); SelectedDate = date;
            Render(); OpenDayPopup(date, DayAgendaPopup.AnchorPoint(anchor));
        }
        private static bool MeaningfulQa(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value != "빈칸" && value != "확인 불가" &&
                !value.StartsWith("미연결", StringComparison.Ordinal) && !value.StartsWith("이벤트 연결", StringComparison.Ordinal);
        }
        private static bool HasQa(CalendarEvent item)
        {
            return item != null && ((item.RemoteStatus != null && item.RemoteStatus.Values.Any(MeaningfulQa)) ||
                (item.RemoteStatus == null && item.Fields != null && item.Fields.Values.Any(MeaningfulQa)));
        }
        private List<EventView> FilterViews(List<EventView> views)
        {
            string query = (Configuration.SearchText ?? "").Trim();
            string filter = Configuration.QaFilter ?? "all";
            return views.Where(view => {
                var item = Snapshot.Events.FirstOrDefault(e => e.Id == view.Id);
                if (item == null) return false;
                bool pending = view.StatusContext != null && StatusDrafts.Drafts.Any(d => d.Key == view.StatusContext.CreateDraft().Key && !d.Superseded && d.State != "complete");
                bool match = filter == "all" || filter == "recorded" && HasQa(item) || filter == "empty" && !HasQa(item) ||
                    filter == "pending" && pending || filter == "conflict" && !String.IsNullOrEmpty(item.StatusConflict);
                if (!match || query.Length == 0) return match;
                return (item.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.Fields != null && item.Fields.Values.Any(v => (v ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    item.RemoteStatus != null && item.RemoteStatus.Values.Any(v => (v ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }).ToList();
        }
        private int PendingCount()
        {
            return StatusDrafts.Drafts.Count(d => !d.Superseded && d.State != "complete");
        }
        private void UpdateViewSummary()
        {
            if (Snapshot == null) return;
            int recorded = Snapshot.Events.Count(HasQa);
            int conflicts = Snapshot.Events.Count(e => !String.IsNullOrEmpty(e.StatusConflict));
            Find<TextBlock>("QaSummaryText").Text = "QA 상태: 기록 있는 일정 " + recorded + "건";
            Find<TextBlock>("SyncSummaryText").Text = "동기화: " + (failed ? "읽기 실패 · 이전 표시" : Snapshot.Mode == "google" ? "Google 읽기" : "로컬 표시") +
                " · PC 보류 " + PendingCount() + "건 · 충돌 " + conflicts + "건";
        }
        private void ShowSafeDiagnostics()
        {
            int total = Snapshot == null ? 0 : Snapshot.Calendars.Count;
            int read = Snapshot == null ? 0 : Snapshot.Calendars.Count(c => c.ReadSucceeded);
            int conflict = Snapshot == null ? 0 : Snapshot.Events.Count(e => !String.IsNullOrEmpty(e.StatusConflict));
            var last = changeTracker.LastSuccessUtc.HasValue ? CalendarRules.InSeoul(changeTracker.LastSuccessUtc.Value).ToString("yyyy.MM.dd HH:mm") : "없음";
            Find<TextBlock>("ConnectionDiagnosticsText").Text = "연결 진단 · 프로필 " + total + "개 · 읽기 성공 " + read + "개 · 마지막 성공 " + last +
                " · PC 보류 " + PendingCount() + "건 · 충돌 " + conflict + "건. 계정·토큰·일정 내용은 표시하지 않습니다.";
            Find<TextBlock>("ConnectionDiagnosticsText").BringIntoView();
        }
        private void RefreshPresets(WidgetViewPreset selected)
        {
            var combo = Find<ComboBox>("PresetSelect"); combo.ItemsSource = null; combo.ItemsSource = Configuration.ViewPresets; combo.SelectedItem = selected;
        }
        private void SaveViewPreset()
        {
            string name = (Find<TextBox>("PresetName").Text ?? "").Trim();
            if (name.Length == 0 || name.Length > 40) { statusText.Text = "보기 프리셋 이름을 1~40자로 입력하세요."; return; }
            var old = Configuration.ViewPresets.FirstOrDefault(p => String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (old != null) Configuration.ViewPresets.Remove(old);
            var preset = new WidgetViewPreset { Name = name, SelectedProfileId = Configuration.SelectedProfileId,
                VisibleCalendarKeys = Configuration.VisibleCalendarKeys == null ? null : Configuration.VisibleCalendarKeys.ToList(),
                SearchText = Configuration.SearchText, QaFilter = Configuration.QaFilter, ViewDensity = Configuration.ViewDensity,
                Width = Window.Width, Height = Window.Height };
            Configuration.ViewPresets.Add(preset); RefreshPresets(preset); Save(); statusText.Text = "현재 보기를 저장했습니다.";
        }
        private void ApplyViewPreset()
        {
            var preset = Find<ComboBox>("PresetSelect").SelectedItem as WidgetViewPreset;
            if (preset == null) { statusText.Text = "불러올 보기 프리셋을 선택하세요."; return; }
            applyingViewPreset = true;
            try {
                Configuration.SelectedProfileId = preset.SelectedProfileId;
                Configuration.VisibleCalendarKeys = preset.VisibleCalendarKeys == null ? null : preset.VisibleCalendarKeys.ToList();
                Configuration.SearchText = preset.SearchText ?? "";
                Configuration.QaFilter = preset.QaFilter ?? "all";
                Configuration.ViewDensity = preset.ViewDensity == "compact" ? "compact" : "comfortable";
                Find<TextBox>("SearchBox").Text = Configuration.SearchText;
                SelectTag(Find<ComboBox>("QaFilterSelect"), Configuration.QaFilter);
                SelectTag(Find<ComboBox>("DensitySelect"), Configuration.ViewDensity);
                foreach (var option in calendarOptions) option.IsSelected = Configuration.VisibleCalendarKeys == null || Configuration.VisibleCalendarKeys.Contains(option.Id);
                var choices = Find<ItemsControl>("CalendarChoices"); choices.ItemsSource = null; choices.ItemsSource = calendarOptions;
                calendarSelect.SelectedValue = Configuration.SelectedProfileId;
                var work = SystemParameters.WorkArea;
                Window.Width = Clamp(preset.Width, Window.MinWidth, work.Width);
                Window.Height = Clamp(preset.Height, Window.MinHeight, work.Height);
            } finally { applyingViewPreset = false; }
            Render(); Save(); statusText.Text = "보기 프리셋을 불러왔습니다.";
        }
        private void DeleteViewPreset()
        {
            var preset = Find<ComboBox>("PresetSelect").SelectedItem as WidgetViewPreset;
            if (preset == null) return;
            Configuration.ViewPresets.Remove(preset); RefreshPresets(null); Save(); statusText.Text = "보기 프리셋을 삭제했습니다.";
        }
    }
}
