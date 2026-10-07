using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Markup;

namespace CalendarWidget
{
    public sealed class ManualCalendarChoice
    {
        public string AccountId { get; set; }
        public string AccountSubject { get; set; }
        public string CalendarId { get; set; }
        public string Label { get; set; }
        public override string ToString() { return Label; }
    }
    public sealed class ManualEventEditor
    {
        public readonly Window Window;
        public readonly ComboBox CalendarSelect;
        public readonly Button SaveButton;
        public Task CurrentAction { get; private set; }
        public Func<ManualEventInput, bool> ConfirmCreate;
        public Func<bool> ConfirmDiscard;
        public Func<ManualEventInput, Task<ManualEventRecord>> Submit;
        public Func<Task> Refresh;
        private bool busy, complete, approved, touched, settingChoices;
        private ManualEventInput pendingInput;
        public ManualEventEditor(Window owner, string root, DateTime date, IList<ManualCalendarChoice> choices)
        {
            using (var input = File.OpenRead(Path.Combine(root, "manual-event.xaml"))) Window = (Window)XamlReader.Load(input);
            Window.Owner = owner;
            ThemeManager.ApplyFromOwner(Window, owner);
            CalendarSelect = Find<ComboBox>("ManualCalendarSelect"); CalendarSelect.ItemsSource = choices;
            AutomationProperties.SetName(CalendarSelect, "등록할 캘린더");
            if (choices.Count > 0) CalendarSelect.SelectedIndex = 0;
            SaveButton = Find<Button>("ManualSaveButton"); SaveButton.IsEnabled = choices.Count > 0;
            Find<DatePicker>("ManualStartDate").SelectedDate = date.Date;
            Find<DatePicker>("ManualEndDate").SelectedDate = date.Date;
            Find<TextBox>("ManualStartTime").Text = "10:00";
            Find<TextBox>("ManualEndTime").Text = "11:00";
            Find<TextBox>("ManualTitle").TextChanged += delegate { touched = true; };
            Find<TextBox>("ManualStartTime").TextChanged += delegate { touched = true; };
            Find<TextBox>("ManualEndTime").TextChanged += delegate { touched = true; };
            foreach (var name in new[] { "ManualStatusSetup", "ManualStatusAlphaCheck", "ManualStatusAlphaTester", "ManualStatusRealCheck", "ManualStatusRealTester" })
                Find<TextBox>(name).TextChanged += delegate { touched = true; };
            Find<DatePicker>("ManualStartDate").SelectedDateChanged += delegate { touched = true; };
            Find<DatePicker>("ManualEndDate").SelectedDateChanged += delegate { touched = true; };
            CalendarSelect.SelectionChanged += delegate { if (!settingChoices) touched = true; };
            Find<CheckBox>("ManualAllDay").Checked += delegate { UpdateAllDay(); };
            Find<CheckBox>("ManualAllDay").Unchecked += delegate { UpdateAllDay(); };
            SaveButton.Click += delegate { if (!busy && !complete) CurrentAction = SaveAsync(); };
            Find<Button>("ManualCancelButton").Click += delegate { Window.Close(); };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (busy) { e.Cancel = true; return; }
                if (!complete && (touched || approved)) {
                    bool discard = ConfirmDiscard != null ? ConfirmDiscard() : MessageBox.Show(Window, "저장하지 않은 일정 입력을 버리시겠습니까?", "일정 입력", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    if (!discard) e.Cancel = true;
                }
            };
            if (choices.Count == 0) Find<TextBlock>("ManualResult").Text = "연결된 Google 캘린더의 권한을 확인하는 중…";
        }
        public void SetChoices(IList<ManualCalendarChoice> choices)
        {
            if (busy || approved || complete) return;
            settingChoices = true;
            CalendarSelect.ItemsSource = choices;
            if (choices.Count > 0) CalendarSelect.SelectedIndex = 0;
            settingChoices = false;
            SaveButton.IsEnabled = choices.Count > 0;
            Find<TextBlock>("ManualResult").Text = choices.Count == 0 ? "쓰기 권한이 확인된 연결 캘린더가 없습니다. 설정에서 계정과 캘린더를 연결하세요." : "";
        }
        public void ShowLoadFailure() { Find<TextBlock>("ManualResult").Text = "캘린더 권한 조회에 실패했습니다. 연결 상태를 확인하고 다시 여세요."; }
        public T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private void UpdateAllDay()
        {
            touched = true;
            bool allDay = Find<CheckBox>("ManualAllDay").IsChecked == true;
            Find<Grid>("ManualTimes").Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("ManualPeriodHint").Visibility = allDay ? Visibility.Visible : Visibility.Collapsed;
        }
        private ManualEventInput ReadInput()
        {
            var choice = CalendarSelect.SelectedItem as ManualCalendarChoice;
            var start = Find<DatePicker>("ManualStartDate").SelectedDate;
            var end = Find<DatePicker>("ManualEndDate").SelectedDate;
            if (choice == null || !start.HasValue || !end.HasValue) throw new InvalidDataException("캘린더와 시작·종료 날짜를 선택하세요.");
            var result = new ManualEventInput { AccountSubject = choice.AccountSubject, CalendarId = choice.CalendarId,
                Title = Find<TextBox>("ManualTitle").Text, StartDate = start.Value.Date, EndDate = end.Value.Date,
                StartTime = Find<TextBox>("ManualStartTime").Text, EndTime = Find<TextBox>("ManualEndTime").Text,
                AllDay = Find<CheckBox>("ManualAllDay").IsChecked == true,
                StatusValues = new Dictionary<string, string> { { "setup", Find<TextBox>("ManualStatusSetup").Text },
                    { "alphaCheck", Find<TextBox>("ManualStatusAlphaCheck").Text }, { "alphaTester", Find<TextBox>("ManualStatusAlphaTester").Text },
                    { "realCheck", Find<TextBox>("ManualStatusRealCheck").Text }, { "realTester", Find<TextBox>("ManualStatusRealTester").Text } } };
            result.Validate(); return result;
        }
        private async Task SaveAsync()
        {
            try {
                var input = pendingInput ?? ReadInput();
                if (!approved) {
                    bool confirmed = ConfirmCreate != null ? ConfirmCreate(input) : MessageBox.Show(Window,
                        "다음 캘린더에 일정을 등록하시겠습니까?\n" + (CalendarSelect.SelectedItem as ManualCalendarChoice).Label +
                        "\n" + input.Title + "\n" + input.Start.ToString("yyyy-MM-dd HH:mm") + " ~ " + input.End.ToString("yyyy-MM-dd HH:mm") + " (Asia/Seoul)",
                        "Google 일정 등록 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    if (!confirmed) return;
                    approved = true; pendingInput = input;
                }
                if (Submit == null) throw new InvalidOperationException("일정 연결이 준비되지 않았습니다.");
                busy = true; SaveButton.IsEnabled = false; Find<TextBlock>("ManualResult").Text = "Google 등록 결과를 확인하는 중…";
                var record = await Submit(input);
                complete = record.State == "complete";
                Find<TextBlock>("ManualResult").Text = record.Message + (complete ? "\n일정 ID: " + record.EventId : "\n같은 일정 ID로 다시 확인할 수 있습니다.");
                if (complete && Refresh != null) await Refresh();
            } catch (Exception ex) { Find<TextBlock>("ManualResult").Text = "등록 확인 실패: " + ex.Message; }
            finally { busy = false; SaveButton.IsEnabled = !complete && CalendarSelect.Items.Count > 0; if (pendingInput != null) LockInput(); }
        }
        private void LockInput()
        {
            CalendarSelect.IsEnabled = false; Find<TextBox>("ManualTitle").IsReadOnly = true;
            Find<DatePicker>("ManualStartDate").IsEnabled = false; Find<DatePicker>("ManualEndDate").IsEnabled = false;
            Find<TextBox>("ManualStartTime").IsReadOnly = true; Find<TextBox>("ManualEndTime").IsReadOnly = true;
            Find<CheckBox>("ManualAllDay").IsEnabled = false;
            foreach (var name in new[] { "ManualStatusSetup", "ManualStatusAlphaCheck", "ManualStatusAlphaTester", "ManualStatusRealCheck", "ManualStatusRealTester" }) Find<TextBox>(name).IsReadOnly = true;
        }
    }
}
