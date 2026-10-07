using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CalendarWidget
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var root = AppDomain.CurrentDomain.BaseDirectory;
            RuntimePermissions.OAuthEnabled = args.Contains("--enable-oauth");
            bool verify = args.Length == 2 && args[0] == "--verify";
            try
            {
                if (verify) return Verification.Run(root, Path.GetFullPath(args[1]));
                if (args.Length == 3 && args[0] == "--inspect-workbook") return InspectWorkbook(root, args[1], args[2]);
                string key;
                using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
                bool created;
                using (var mutex = new Mutex(true, @"Local\CalendarWidget_" + key, out created))
                {
                    if (!created)
                    {
                        for (int attempt = 0; attempt < 10; attempt++)
                        {
                            try { using (var existing = EventWaitHandle.OpenExisting(@"Local\" + (args.Contains("--settings") ? "CalendarWidgetSettings_" : "CalendarWidgetShow_") + key)) existing.Set(); return 0; }
                            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
                        }
                        return 2;
                    }
                    using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CalendarWidgetShow_" + key))
                    using (var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CalendarWidgetSettings_" + key))
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        var widget = new Widget(root, false, args.Contains("--cached-only"));
                        app.MainWindow = widget.Window;
                        if (args.Contains("--settings")) widget.Window.Loaded += delegate { widget.Window.Dispatcher.BeginInvoke(new Action(widget.OpenSettings), DispatcherPriority.ApplicationIdle); };
                        if (args.Contains("--diagnostics"))
                        {
                            widget.RuntimeStateChanged = delegate {
                                    var handle = new System.Windows.Interop.WindowInteropHelper(widget.Window).Handle;
                                    var directory = Path.Combine(root, "evidence"); Directory.CreateDirectory(directory);
                                    File.WriteAllText(Path.Combine(directory, "runtime-status.json"), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                                        startupRequested = args.Contains("--startup"), nativeWindowHandle = handle.ToInt64(), wpfVisible = widget.Window.IsVisible,
                                        win32Visible = NativeWindow.IsWindowVisible(handle), showInTaskbar = widget.Window.ShowInTaskbar,
                                        dataMode = widget.Snapshot == null ? "unknown" : widget.Snapshot.Mode, settingsVisible = widget.SettingsVisible,
                                        settingsWindowHandle = widget.SettingsWindowHandle, displayedEvents = widget.Find<ItemsControl>("EventList").Items.Count,
                                        calendarName = widget.Snapshot == null ? null : widget.Snapshot.Calendars[0].Name,
                                        pid = Process.GetCurrentProcess().Id, reportedAtUtc = DateTimeOffset.UtcNow.ToString("o") }));
                                    if (widget.Snapshot != null && widget.Snapshot.Mode == "google" && widget.Window.IsVisible)
                                        Verification.Capture(widget.Window, Path.Combine(directory, "widget-connected-live.png"), 96);
                            };
                            widget.Window.Loaded += delegate { widget.Window.Dispatcher.BeginInvoke(widget.RuntimeStateChanged, DispatcherPriority.ApplicationIdle); };
                        }
                        var signalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        signalTimer.Tick += delegate { if (settingsSignal.WaitOne(0)) widget.OpenSettings(); if (showSignal.WaitOne(0)) widget.Reveal(); };
                        signalTimer.Start();
                        if (args.Contains("--startup"))
                        {
                            var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                            delay.Tick += delegate { delay.Stop(); widget.Window.Show(); };
                            delay.Start();
                        }
                        else widget.Window.Show();
                        app.Run();
                        signalTimer.Stop();
                    }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(root, "startup-error.txt"), DateTimeOffset.UtcNow.ToString("o") + " " + ex.GetType().Name + ": " + ex.Message); } catch { }
                if (!verify) MessageBox.Show("캘린더 위젯을 시작하지 못했습니다. 폴더의 startup-error.txt를 확인하세요.", "캘린더 위젯");
                return 1;
            }
        }
        private static int InspectWorkbook(string root, string sourcePath, string reportPath)
        {
            var configuration = ConfigurationStore.Load(Path.Combine(root, "settings.json"));
            var options = configuration.ExcelOptions; options.DocumentId = configuration.SourceDocumentId; options.Worksheet = configuration.SourceWorksheet;
            string before = SyncHash.Bytes(File.ReadAllBytes(sourcePath));
            var workbook = ExcelEventParser.Read(sourcePath, options);
            var cutoff = new DateTimeOffset(CalendarRules.Cutoff(DateTimeOffset.UtcNow), TimeSpan.FromHours(9));
            var future = workbook.Events.Where(e => (e.End ?? e.Start.AddTicks(1)) > cutoff).ToList();
            File.WriteAllText(reportPath, JsonData.Serialize(new { readOnly = true, sourceFile = sourcePath, sourceHash = workbook.FileHash,
                sourceUnchanged = before == SyncHash.Bytes(File.ReadAllBytes(sourcePath)), modifiedAtUtc = workbook.FileModifiedAt, observedAtUtc = workbook.ObservedAt,
                totalEvents = workbook.Events.Count, futureEvents = future.Count, warnings = workbook.Warnings,
                events = future.Select(e => new { title = e.Title, baseTitle = e.BaseTitle, phase = e.Phase, start = e.Start.ToString("o"), end = e.End.HasValue ? e.End.Value.ToString("o") : null, mappingStatus = e.MappingStatus, fields = e.Fields }), actualGoogleWrites = false }));
            configuration.SourceKind = "local"; configuration.SourceWorkbookPath = Path.GetFullPath(sourcePath); configuration.UseWorkbook = future.Count > 0;
            ConfigurationStore.Save(Path.Combine(root, "settings.json"), configuration); return 0;
        }
    }
    internal static class NativeWindow
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr handle);
    }
    public sealed class Widget : IDisposable
    {
        public readonly Window Window;
        public ICalendarSource Source;
        public CalendarSnapshot Snapshot { get; private set; }
        public readonly WidgetConfiguration Configuration;
        public readonly string Root;
        private SettingsDialog activeSettings;
        private StatusEditor activeStatusEditor;
        private ManualEventEditor activeManualEditor;
        public ManualEventEditor ActiveManualEditor { get { return activeManualEditor; } }
        public Task ManualChoicesAction { get; private set; }
        public DayAgendaPopup ActiveDayPopup { get; private set; }
        public string LastPopupCloseReason { get; private set; }
        public StatusDraftStore StatusDrafts { get; private set; }
        public void OpenStatusEditor(EventView view)
        {
            if (activeStatusEditor != null && activeStatusEditor.Window.IsVisible) { activeStatusEditor.Window.Activate(); return; }
            var current = Snapshot == null ? null : Snapshot.Events.SingleOrDefault(e => e.Id == view.Id);
            if (current == null || current.StatusContext == null || view.StatusContext == null || current.StatusContext.CreateDraft().Key != view.StatusContext.CreateDraft().Key || current.StatusContext.Binding.Signature != view.StatusContext.Binding.Signature || current.StatusContext.GoogleVersion != view.StatusContext.GoogleVersion || current.StatusContext.Binding.SourceHash != view.StatusContext.Binding.SourceHash) { statusText.Text = "편집 대상이 바뀌었습니다. 새로고침 후 다시 선택하세요."; return; }
            try {
                activeStatusEditor = new StatusEditor(Window, view, StatusDrafts, CreateStatusEndpoints(view)); activeStatusEditor.Window.ShowDialog();
                var saved = activeStatusEditor.LastSavedDraft;
                if (saved != null && saved.GoogleDone) changeTracker.Suppress(ConnectedCalendarSource.CalendarKey(saved.AccountId, saved.CalendarId) + ":" + saved.GoogleEventId, Now);
            }
            catch { statusText.Text = "상태 편집을 열 수 없습니다. 원본 행과 보류 변경을 확인하세요."; }
            finally { activeStatusEditor = null; ApplyDeferredSnapshot(); Render(); if (!verification && Configuration.UseWorkbook && Configuration.SourceKind == "local") Window.Dispatcher.BeginInvoke(new Action(async delegate { await RefreshSource(); }), DispatcherPriority.ApplicationIdle); }
        }
        private StatusEndpointConnection CreateStatusEndpoints(EventView view)
        {
            return verification || view.StatusContext == null ? null : StatusEndpointConnection.Create(Root, Configuration, view.StatusContext);
        }
        public void OpenManualEventEditor(DateTime date)
        {
            if (activeManualEditor != null && activeManualEditor.Window.IsVisible) { activeManualEditor.Window.Activate(); return; }
            var editor = new ManualEventEditor(Window, Root, date, new List<ManualCalendarChoice>());
            activeManualEditor = editor;
            editor.Submit = async input => {
                var choice = editor.CalendarSelect.SelectedItem as ManualCalendarChoice;
                if (choice == null || choice.AccountSubject != input.AccountSubject || choice.CalendarId != input.CalendarId)
                    throw new InvalidOperationException("선택한 캘린더 연결을 다시 확인하세요.");
                var account = Configuration.Accounts.Single(a => a.Id == choice.AccountId && a.GoogleSubject == input.AccountSubject);
                using (var transport = new HttpTransport()) {
                    var workflow = new SourceWorkflow(Root, transport);
                    var client = OAuthClientSettings.Google(Configuration.OAuthClientFile);
                    var token = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<OAuthToken>(workflow.Credentials.Load("google-" + account.Id));
                    bool scope = token != null && token.ClientId == client.ClientId && CalendarAccess.HasEventWriteScope(token.Scopes);
                    var google = workflow.GoogleExisting(Configuration, account);
                    var store = new ManualEventStore(Path.Combine(Root, ".state", "manual-events.json"));
                    var created = await new ManualEventService(google, google.Calendars, store, scope,
                        record => new GoogleStatusEndpoint(google, record.AccountSubject, record.CalendarId, scope), StatusDrafts)
                        .CreateOrReconcile(input, CancellationToken.None);
                    if (created.State == "complete") changeTracker.Suppress(ConnectedCalendarSource.CalendarKey(created.AccountSubject, created.CalendarId) + ":" + created.EventId, Now);
                    return created;
                }
            };
            editor.Refresh = async () => { if (!cachedOnly) await RefreshSource(); };
            editor.Window.Closed += delegate { if (activeManualEditor == editor) activeManualEditor = null; ApplyDeferredSnapshot(); };
            editor.Window.Show();
            ManualChoicesAction = LoadManualChoices(editor);
        }
        private async Task LoadManualChoices(ManualEventEditor editor)
        {
            try {
                var choices = await ManualCalendarDiscovery.FromExistingGrant(Root, Configuration, connectedReadCancellation.Token);
                if (editor.Window.IsVisible && activeManualEditor == editor) editor.SetChoices(choices);
            } catch { if (editor.Window.IsVisible && activeManualEditor == editor) editor.ShowLoadFailure(); }
        }
        public Action RuntimeStateChanged;
        public bool SettingsVisible { get { return activeSettings != null && activeSettings.Window.IsVisible; } }
        public long SettingsWindowHandle { get { return activeSettings == null ? 0 : new System.Windows.Interop.WindowInteropHelper(activeSettings.Window).Handle.ToInt64(); } }
        public void OpenSettings()
        {
            if (SettingsVisible) { activeSettings.Window.Activate(); return; }
            Reveal(); activeSettings = new SettingsDialog(this);
            activeSettings.Window.Loaded += delegate { if (RuntimeStateChanged != null) RuntimeStateChanged(); };
            try { activeSettings.Window.ShowDialog(); }
            finally { activeSettings = null; ApplyDeferredSnapshot(); if (!verification && !cachedOnly) { nextUpdateCheck = DateTimeOffset.MinValue; Window.Dispatcher.BeginInvoke(new Action(async delegate { await CheckForUpdates(); })); } if (RuntimeStateChanged != null) RuntimeStateChanged(); }
        }
        public readonly string VerificationStateFile = "ui-state-" + Guid.NewGuid().ToString("N") + ".json";
        public bool IsVerification { get { return verification; } }
        public WorkbookEvents LastWorkbook { get; private set; }
        public void DisplayWorkbook(WorkbookEvents workbook, string mode) { LastWorkbook = workbook; Source = new WorkbookDisplaySource(workbook, mode, Configuration, Root); Reload(); }
        public void DisplayConnected(CalendarSnapshot snapshot) { LastWorkbook = null; Source = new SnapshotSource(snapshot); Reload(); if (RuntimeStateChanged != null) RuntimeStateChanged(); }
        public void ResetToSample() { LastWorkbook = null; Source = new SampleProvider(Path.Combine(Root, "sample-data.json")).CreateSource(null); Reload(); }
        private readonly bool verification;
        private readonly bool cachedOnly;
        private readonly string configPath;
        private readonly ComboBox calendarSelect;
        private List<CalendarChoiceOption> calendarOptions = new List<CalendarChoiceOption>();
        private readonly ItemsControl eventList, monthDays;
        private readonly TextBlock monthTitle, dayHeading;
        private DateTime displayedMonth; public DateTime? SelectedDate { get; private set; }
        private readonly TextBlock clockText, countText, statusText, emptyText;
        private readonly Border emptyPanel;
        private readonly Button pinButton;
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer remoteTimer;
        private readonly CalendarChangeTracker changeTracker = new CalendarChangeTracker();
        private readonly CalendarPollBackoff pollBackoff = new CalendarPollBackoff();
        private Task currentReadTask;
        private string currentReadScope;
        private CalendarSnapshot deferredSnapshot;
        private CalendarChangeSummary deferredChanges;
        private readonly TextBlock lastSuccessText, changeSummary;
        private readonly Border changeBanner;
        private readonly Border updateBanner;
        private readonly TextBlock updateSummary, versionText;
        private UpdateRelease availableUpdate;
        private string availableUpdateFeed;
        private string dismissedUpdateVersion;
        private DateTimeOffset nextUpdateCheck;
        private bool updateChecking, updateApplying;
        private readonly CancellationTokenSource updateCancellation = new CancellationTokenSource();
        public Func<UpdateRelease, Task<bool>> SyntheticUpdateApply;
        private Forms.NotifyIcon tray;
        private bool loading, failed;
        private bool sourceReading, disposed;
        private readonly CancellationTokenSource connectedReadCancellation = new CancellationTokenSource();
        public DateTimeOffset? FixedNow { get; set; }
        public DateTimeOffset Now { get { return FixedNow ?? DateTimeOffset.UtcNow; } }
        public Widget(string root, bool verification, bool cachedOnly = false)
        {
            Root = root; this.verification = verification; this.cachedOnly = cachedOnly; configPath = Path.Combine(root, "settings.json");
            StatusDrafts = new StatusDraftStore(Path.Combine(root, ".state", verification ? "status-drafts-test-" + Guid.NewGuid().ToString("N") + ".json" : "status-drafts.json"));
            bool configError = false;
            try { Configuration = verification ? new WidgetConfiguration() : ConfigurationStore.Load(configPath); }
            catch { Configuration = new WidgetConfiguration(); configError = true; }
            Source = new SampleProvider(Path.Combine(root, "sample-data.json")).CreateSource(null);
            if (!verification && Configuration.UseConnectedCalendar) {
                CalendarSnapshot cached = null;
                try { cached = new LocalSampleSource(Path.Combine(root, ".cache/google-calendar.json")).Load(); if (cached.Mode != "google" || cached.Scope != ConnectedCalendarSource.Scope(Configuration)) cached = null; } catch { }
                Source = new SnapshotSource(cached ?? ConnectedCalendarSource.Empty(Configuration));
            }
            using (var input = File.OpenRead(Path.Combine(root, "widget.xaml"))) Window = (Window)XamlReader.Load(input);
            ThemeManager.Apply(Window, Configuration);
            ThemeManager.AppearanceChanged += HandleAppearanceChanged;
            displayedMonth = new DateTime(CalendarRules.InSeoul(Now).Year, CalendarRules.InSeoul(Now).Month, 1);
            calendarSelect = Find<ComboBox>("CalendarSelect"); eventList = Find<ItemsControl>("EventList"); monthDays = Find<ItemsControl>("MonthDays");
            monthTitle = Find<TextBlock>("MonthTitle"); dayHeading = Find<TextBlock>("DayHeading");
            clockText = Find<TextBlock>("ClockText"); countText = Find<TextBlock>("CountText"); statusText = Find<TextBlock>("StatusText");
            emptyText = Find<TextBlock>("EmptyText"); emptyPanel = Find<Border>("EmptyPanel"); pinButton = Find<Button>("PinButton");
            lastSuccessText = Find<TextBlock>("LastSuccessText"); changeSummary = Find<TextBlock>("ChangeSummary"); changeBanner = Find<Border>("ChangeBanner");
            updateBanner = Find<Border>("UpdateBanner"); updateSummary = Find<TextBlock>("UpdateSummary"); versionText = Find<TextBlock>("VersionText");
            versionText.Text = "앱 버전 " + UpdateCore.VersionText + " · 업데이트 확인 전";
            Find<Button>("UpdateDismissButton").Click += delegate { if (availableUpdate != null) dismissedUpdateVersion = availableUpdate.Version; updateBanner.Visibility = Visibility.Collapsed; };
            Find<Button>("UpdateApplyButton").Click += async delegate { await ApplyAvailableUpdate(); };
            Find<Button>("DismissChangeButton").Click += delegate { changeBanner.Visibility = Visibility.Collapsed; };
            var work = SystemParameters.WorkArea;
            Window.Width = Clamp(Configuration.Width, Window.MinWidth, work.Width);
            Window.Height = Clamp(Configuration.Height, Window.MinHeight, work.Height);
            Window.Left = Configuration.Left == -1 ? work.Right - Window.Width - 20 : Clamp(Configuration.Left, work.Left, work.Right - Window.Width);
            Window.Top = Configuration.Top == -1 ? work.Top + 20 : Clamp(Configuration.Top, work.Top, work.Bottom - Window.Height);
            Window.Topmost = Configuration.Pinned; UpdatePin();
            Find<Grid>("DragHeader").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.OriginalSource is TextBlock || e.OriginalSource is Grid || e.OriginalSource is StackPanel)
                    try { Window.DragMove(); } catch (InvalidOperationException) { }
            };
            pinButton.Click += delegate { Window.Topmost = !Window.Topmost; Configuration.Pinned = Window.Topmost; UpdatePin(); Save(); };
            Find<Button>("HideButton").Click += delegate { if (tray != null) Window.Hide(); else statusText.Text = "트레이를 사용할 수 없어 숨기지 않았습니다."; };
            Find<Button>("CloseButton").Click += delegate { Window.Close(); };
            Find<Button>("RefreshButton").Click += async delegate { await RefreshSource(); };
            Find<Button>("AddEventButton").Click += delegate { OpenManualEventEditor(SelectedDate ?? CalendarRules.InSeoul(Now).Date); };
            Window.AddHandler(Button.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e) {
                var button = e.OriginalSource as Button;
                var day = button == null ? null : button.DataContext as MonthDay;
                if (button != null && button.Name == "DayButton" && day != null) { e.Handled = true; var anchor = DayAgendaPopup.AnchorPoint(button); SelectedDate = day.Date; displayedMonth = new DateTime(day.Date.Year, day.Date.Month, 1); Render(); OpenDayPopup(day.Date, anchor); return; }
                var view = button == null ? null : button.DataContext as EventView;
                if (button != null && button.Name == "EditStatusButton" && view != null && view.CanEdit) { e.Handled = true; OpenStatusEditor(view); }
            }));
            Find<Button>("PreviousMonthButton").Click += delegate { displayedMonth = MonthCalendar.Move(displayedMonth, -1); SelectedDate = null; Render(); Find<ScrollViewer>("EventScroll").ScrollToTop(); };
            Find<Button>("NextMonthButton").Click += delegate { displayedMonth = MonthCalendar.Move(displayedMonth, 1); SelectedDate = null; Render(); Find<ScrollViewer>("EventScroll").ScrollToTop(); };
            Find<Button>("TodayButton").Click += delegate { var today = CalendarRules.InSeoul(Now).Date; displayedMonth = new DateTime(today.Year, today.Month, 1); SelectedDate = null; Render(); Find<ScrollViewer>("EventScroll").ScrollToTop(); };
            Find<Button>("FilterButton").Click += delegate { var pane = Find<Border>("FilterPane"); pane.Visibility = pane.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; };
            Find<Button>("SelectAllCalendarsButton").Click += delegate { SetAllCalendars(true); };
            Find<Button>("ClearCalendarsButton").Click += delegate { SetAllCalendars(false); };
            Window.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(delegate(object sender, RoutedEventArgs e) {
                var box = e.OriginalSource as CheckBox;
                var option = box == null ? null : box.DataContext as CalendarChoiceOption;
                if (box == null || box.Name != "CalendarChoiceCheck" || option == null) return;
                option.IsSelected = box.IsChecked == true;
                SaveCalendarChoices();
            }));
            Find<Button>("SettingsButton").Click += delegate
            {
                OpenSettings();
            };
            calendarSelect.SelectionChanged += delegate
            {
                if (loading) return;
                if (ActiveDayPopup != null && ActiveDayPopup.Window.IsVisible) ActiveDayPopup.Window.Close();
                var selected = calendarSelect.SelectedItem as CalendarProfile;
                Configuration.SelectedProfileId = selected == null ? "" : selected.Id;
                Render(); Save();
            };
            Window.Closing += delegate { if (ActiveDayPopup != null && ActiveDayPopup.Window.IsVisible) ActiveDayPopup.Window.Close(); Configuration.Left = Window.Left; Configuration.Top = Window.Top; Configuration.Width = Window.Width; Configuration.Height = Window.Height; Save(); Dispose(); };
            if (!verification)
            {
                try
                {
                    tray = new Forms.NotifyIcon { Text = "캘린더 위젯 · 샘플", Icon = System.Drawing.SystemIcons.Application, Visible = true };
                    var menu = new Forms.ContextMenuStrip();
                    menu.Items.Add("위젯 표시", null, delegate { Window.Dispatcher.BeginInvoke(new Action(Reveal)); });
                    menu.Items.Add("종료", null, delegate { Window.Dispatcher.BeginInvoke(new Action(Window.Close)); });
                    tray.ContextMenuStrip = menu;
                    tray.DoubleClick += delegate { Window.Dispatcher.BeginInvoke(new Action(Reveal)); };
                }
                catch { if (tray != null) tray.Dispose(); tray = null; }
            }
            Reload();
            if (!verification) Window.Loaded += async delegate { await RefreshSource(); if (!cachedOnly) await CheckForUpdates(); };
            if (configError) statusText.Text = "설정 파일 오류 · 기본 설정으로 시작했습니다.";
            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += delegate { Render(); };
            if (!verification) timer.Start();
            remoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            remoteTimer.Tick += async delegate {
                if (!disposed && !cachedOnly && Configuration.UseConnectedCalendar && pollBackoff.Ready(Now) && !EditingWindowOpen()) await RefreshSource();
                if (!disposed && !cachedOnly && Now >= nextUpdateCheck) await CheckForUpdates();
            };
            if (!verification) remoteTimer.Start();
        }
        public T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private void SetAllCalendars(bool selected)
        {
            foreach (var option in calendarOptions) option.IsSelected = selected;
            Find<ItemsControl>("CalendarChoices").ItemsSource = null;
            Find<ItemsControl>("CalendarChoices").ItemsSource = calendarOptions;
            SaveCalendarChoices();
        }
        private void SaveCalendarChoices()
        {
            Configuration.VisibleCalendarKeys = calendarOptions.Where(o => o.IsSelected).Select(o => o.Id).ToList();
            calendarSelect.SelectedIndex = 0;
            Configuration.SelectedProfileId = "";
            Render(); Save();
        }
        private void OpenDayPopup(DateTime date, Point anchor)
        {
            if (ActiveDayPopup != null && ActiveDayPopup.Window.IsVisible) {
                if (ActiveDayPopup.Date == date.Date) { ActiveDayPopup.Window.Activate(); return; }
                ActiveDayPopup.Window.Close();
            }
            var views = eventList.Items.OfType<EventView>().ToList();
            var popup = new DayAgendaPopup(Window, Root, date, views, OpenStatusEditor, OpenManualEventEditor);
            ActiveDayPopup = popup;
            popup.Window.Closed += delegate {
                if (ActiveDayPopup != popup) return;
                LastPopupCloseReason = popup.CloseReason;
                ActiveDayPopup = null;
                ApplyDeferredSnapshot();
                if (!disposed && Window.IsVisible && activeStatusEditor == null) FocusDay(date);
            };
            popup.ShowAt(anchor);
        }
        private void FocusDay(DateTime date)
        {
            monthDays.UpdateLayout();
            var day = monthDays.Items.OfType<MonthDay>().FirstOrDefault(d => d.Date == date.Date);
            var container = day == null ? null : monthDays.ItemContainerGenerator.ContainerFromItem(day);
            var button = FindChildButton(container);
            if (button != null) button.Focus(); else Find<Button>("TodayButton").Focus();
        }
        private static Button FindChildButton(DependencyObject root)
        {
            if (root == null) return null;
            var button = root as Button; if (button != null && button.Name == "DayButton") return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
                var found = FindChildButton(VisualTreeHelper.GetChild(root, i)); if (found != null) return found;
            }
            return null;
        }
        public static double Clamp(double value, double min, double max) { return Double.IsNaN(value) || Double.IsInfinity(value) ? min : Math.Max(min, Math.Min(Math.Max(min, max), value)); }
        private void UpdatePin() { pinButton.Content = Window.Topmost ? "⌃" : "⌄"; pinButton.ToolTip = Window.Topmost ? "항상 위 표시 해제" : "다른 창 위에도 표시"; }
        public void Reveal() { Window.Show(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        private bool EditingWindowOpen()
        {
            return activeStatusEditor != null && activeStatusEditor.Window.IsVisible ||
                activeManualEditor != null && activeManualEditor.Window.IsVisible ||
                activeSettings != null && activeSettings.Window.IsVisible ||
                ActiveDayPopup != null && ActiveDayPopup.Window.IsVisible;
        }
        public async Task CheckForUpdates()
        {
            if (disposed || updateChecking || updateApplying) return;
            updateChecking = true;
            nextUpdateCheck = Now.AddMinutes(5);
            var feed = Configuration.ReleaseFeed;
            try {
                if (String.IsNullOrWhiteSpace(feed)) {
                    availableUpdate = null; availableUpdateFeed = null; updateBanner.Visibility = Visibility.Collapsed;
                    versionText.Text = "앱 버전 " + UpdateCore.VersionText + " · 업데이트 위치 미설정";
                    return;
                }
                var found = await UpdateCore.Check(feed, updateCancellation.Token);
                if (disposed || feed != Configuration.ReleaseFeed) return;
                if (found != null && UpdateCore.IsNewer(found)) {
                    availableUpdate = found;
                    availableUpdateFeed = feed;
                    versionText.Text = "앱 버전 " + UpdateCore.VersionText + " · 새 버전 " + found.Version;
                    updateSummary.Text = "새 버전 " + found.Version + " 사용 가능";
                    updateBanner.Visibility = dismissedUpdateVersion == found.Version ? Visibility.Collapsed : Visibility.Visible;
                } else {
                    availableUpdate = null; availableUpdateFeed = null; updateBanner.Visibility = Visibility.Collapsed;
                    versionText.Text = "앱 버전 " + UpdateCore.VersionText + " · 최신 버전 확인";
                }
            } catch (OperationCanceledException) { }
            catch {
                if (!disposed && feed == Configuration.ReleaseFeed) { availableUpdate = null; availableUpdateFeed = null;
                    updateBanner.Visibility = Visibility.Collapsed; versionText.Text = "앱 버전 " + UpdateCore.VersionText + " · 업데이트 확인 실패"; }
            } finally { updateChecking = false; }
        }
        private async Task ApplyAvailableUpdate()
        {
            if (updateApplying || availableUpdate == null || disposed || (!verification && availableUpdateFeed != Configuration.ReleaseFeed)) return;
            if (EditingWindowOpen()) { statusText.Text = "열린 편집 창을 닫은 뒤 업데이트를 적용하세요."; return; }
            updateApplying = true;
            var button = Find<Button>("UpdateApplyButton"); button.IsEnabled = false;
            try {
                if (verification && SyntheticUpdateApply != null) {
                    await SyntheticUpdateApply(availableUpdate);
                    return;
                }
                if (MessageBox.Show(Window, "앱을 재시작해 " + availableUpdate.Version + " 버전을 적용할까요?", "캘린더 위젯 업데이트",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var package = await UpdateCore.Prepare(Root, Configuration.ReleaseFeed, availableUpdate, updateCancellation.Token);
                if (disposed || EditingWindowOpen()) return;
                var runnerDirectory = Path.Combine(Root, ".updates", "runner"); Directory.CreateDirectory(runnerDirectory);
                var runner = Path.Combine(runnerDirectory, "updater-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Path.Combine(Root, "CalendarWidgetUpdater.exe"), runner);
                var request = Path.Combine(runnerDirectory, "request-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(request, JsonData.Serialize(new UpdateRequest { Root = Root, PackagePath = package,
                    Release = availableUpdate, OldProcessId = Process.GetCurrentProcess().Id,
                    EnableOAuth = Environment.GetCommandLineArgs().Contains("--enable-oauth") }));
                var helper = Process.Start(new ProcessStartInfo(runner, "\"" + request + "\"") { WorkingDirectory = Root,
                    UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                if (helper == null) throw new IOException("Updater did not start.");
                Window.Close();
            } catch (OperationCanceledException) { }
            catch { statusText.Text = "업데이트 준비에 실패했습니다. 기존 앱은 그대로 사용할 수 있습니다."; }
            finally { updateApplying = false; if (!disposed) button.IsEnabled = true; }
        }
        public void ShowSyntheticUpdate(UpdateRelease release)
        {
            if (!verification) throw new InvalidOperationException("Synthetic update is available only in verification.");
            availableUpdate = release; updateSummary.Text = "새 버전 " + release.Version + " 사용 가능";
            updateBanner.Visibility = Visibility.Visible;
        }
        private void UpdateLastSuccess()
        {
            if (changeTracker.LastSuccessUtc.HasValue)
                lastSuccessText.Text = "마지막 서버 확인: " + CalendarRules.InSeoul(changeTracker.LastSuccessUtc.Value).ToString("yyyy.MM.dd HH:mm") + " (한국 시간)";
        }
        private void ShowChanges(CalendarChangeSummary changes)
        {
            DateTimeOffset pause;
            bool paused = DateTimeOffset.TryParse(Configuration.ChangeAlertsPausedUntilUtc, out pause) && pause > Now;
            bool enabled = Configuration.ChangeAlertsEnabled && !paused;
            if (!enabled) return;
            if (changes.Total > 0) {
                changeSummary.Text = changes.Text;
                changeBanner.Visibility = Visibility.Visible;
                if (tray != null) try { tray.ShowBalloonTip(4000, "캘린더 변경", changes.Text, Forms.ToolTipIcon.Info); } catch { }
            } else if (changes.Conflicts > 0 && lastConflictCount != changes.Conflicts) {
                changeSummary.Text = "Google 상태와 이 PC의 Excel 값이 다른 일정 " + changes.Conflicts + "건 · 자동 덮어쓰기 없음";
                changeBanner.Visibility = Visibility.Visible;
            }
            lastConflictCount = changes.Conflicts;
        }
        private int lastConflictCount;
        private void ApplyDeferredSnapshot()
        {
            if (disposed || deferredSnapshot == null || EditingWindowOpen()) return;
            var snapshot = deferredSnapshot; var changes = deferredChanges;
            deferredSnapshot = null; deferredChanges = null;
            DisplayConnected(snapshot); UpdateLastSuccess();
            if (changes != null) ShowChanges(changes);
        }
        public void AcceptConnectedSnapshot(CalendarSnapshot snapshot)
        {
            var changes = changeTracker.Accept(snapshot, Now);
            pollBackoff.Succeeded(Now);
            if (EditingWindowOpen()) { deferredSnapshot = snapshot; deferredChanges = changes; UpdateLastSuccess(); }
            else { DisplayConnected(snapshot); UpdateLastSuccess(); ShowChanges(changes); }
        }
        public Task RefreshSource()
        {
            string scope = null;
            if (Configuration.UseConnectedCalendar) try { scope = ConnectedCalendarSource.Scope(Configuration); } catch { }
            if (currentReadTask != null && !currentReadTask.IsCompleted) {
                if (scope == currentReadScope) return currentReadTask;
                return RefreshAfterCurrent(currentReadTask);
            }
            currentReadScope = scope;
            currentReadTask = RefreshSourceCore();
            return currentReadTask;
        }
        private async Task RefreshAfterCurrent(Task current)
        {
            await current;
            if (!disposed) await RefreshSource();
        }
        private async Task RefreshSourceCore()
        {
            if (Configuration.UseConnectedCalendar && !verification) {
                if (cachedOnly) { statusText.Text = "저장된 연결 일정 · 온라인 읽기 미실행 · 최신성 미확인"; return; }
                if (sourceReading || disposed) return;
                sourceReading = true; Find<Button>("RefreshButton").IsEnabled = false; statusText.Text = "연결 캘린더를 읽는 중…";
                try {
                    var captured = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<WidgetConfiguration>(JsonData.Serialize(Configuration));
                    var snapshot = await ConnectedCalendarSource.Read(Root, captured, Now, connectedReadCancellation.Token);
                    if (!snapshot.Calendars.Any(c => c.ReadSucceeded)) throw new InvalidOperationException("읽기에 성공한 캘린더가 없습니다.");
                    if (!disposed && ConnectedCalendarSource.Scope(captured) == ConnectedCalendarSource.Scope(Configuration)) {
                        string cachePath = Path.Combine(Root, ".cache/google-calendar.json"); Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                        File.WriteAllText(cachePath + ".tmp", JsonData.Serialize(snapshot)); if (File.Exists(cachePath)) File.Replace(cachePath + ".tmp", cachePath, null); else File.Move(cachePath + ".tmp", cachePath);
                        AcceptConnectedSnapshot(snapshot);
                    }
                } catch { pollBackoff.Failed(Now); if (!disposed) { failed = true; statusText.Text = "연결 읽기 실패 · 저장된 표시 유지 (최신 아님) · 설정에서 연결과 원본을 확인하세요."; } }
                finally { sourceReading = false; if (!disposed) Find<Button>("RefreshButton").IsEnabled = true; }
                return;
            }
            if (!Configuration.UseWorkbook || Configuration.SourceKind != "local") { Reload(); return; }
            if (sourceReading || disposed) return;
            sourceReading = true; Find<Button>("RefreshButton").IsEnabled = false; statusText.Text = "다운로드 원본을 읽는 중…";
            string document = Configuration.SourceDocumentId;
            try {
                var captured = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<WidgetConfiguration>(JsonData.Serialize(Configuration));
                captured.ExcelOptions.DocumentId = captured.SourceDocumentId; captured.ExcelOptions.Worksheet = captured.SourceWorksheet;
                var workbook = await Task.Run(() => ExcelEventParser.Read(captured.SourceWorkbookPath, captured.ExcelOptions));
                if (!disposed && document == Configuration.SourceDocumentId) DisplayWorkbook(workbook, verification ? "synthetic" : "local-xlsx");
            } catch { if (!disposed) { failed = true; statusText.Text = "원본 다시 읽기 실패 · 이전 표시 유지 (최신 아님) · 최신 xlsx를 선택하세요."; } }
            finally { sourceReading = false; if (!disposed) Find<Button>("RefreshButton").IsEnabled = true; }
        }
        public void Reload()
        {
            try
            {
                if (ActiveDayPopup != null && ActiveDayPopup.Window.IsVisible) ActiveDayPopup.Window.Close();
                var data = Source.Load();
                Snapshot = data; failed = false;
                loading = true;
                bool connected = data.Mode == "google" || data.Mode == "google-loading";
                var choices = CalendarSelection.Choices(data);
                calendarSelect.ItemsSource = choices;
                calendarOptions = choices.Skip(1).Select(c => new CalendarChoiceOption { Id = c.Id, Name = c.Name,
                    IsSelected = Configuration.VisibleCalendarKeys == null || Configuration.VisibleCalendarKeys.Contains(c.Id) }).ToList();
                Find<ItemsControl>("CalendarChoices").ItemsSource = calendarOptions;
                Configuration.SelectedProfileId = CalendarSelection.Resolve(data, Configuration.SelectedProfileId);
                calendarSelect.SelectedValue = Configuration.SelectedProfileId;
                loading = false;
                Render();
                bool sample = data.Mode == "sample" || data.Mode == "synthetic";
                Find<TextBlock>("ModeTitle").Text = sample ? "샘플 데이터 · 실제 일정 연결 전" : connected ? (data.Mode == "google-loading" ? "연결 캘린더를 불러오는 중" : "Google 연결 일정 · 상태는 원본 스냅샷") : "원본 읽기 스냅샷 · Google 동기화는 별도";
                Find<TextBlock>("ModeDescription").Text = sample ? "표시된 일정과 추가 필드는 모두 예시입니다." : "불러온 시각: " + CalendarRules.InSeoul(DateTimeOffset.Parse(data.ObservedAt)).ToString("yyyy.MM.dd HH:mm") + (LastWorkbook != null && LastWorkbook.FileModifiedAt.HasValue ? "\n로컬 파일 수정: " + CalendarRules.InSeoul(LastWorkbook.FileModifiedAt.Value).ToString("yyyy.MM.dd HH:mm") : "") + " · Asia/Seoul\n" + (connected ? "일정은 Google에서 읽고 상태는 다운로드 원본에서 연결합니다. 전체 원본의 최신성은 미확인입니다." : "서버 최신성과 실시간 변경은 보장하지 않습니다. 최신 파일을 선택하세요.");
                Window.Title = "캘린더 위젯 · " + (sample ? "샘플" : connected ? "연결 일정" : "원본 읽기");
                if (tray != null) tray.Text = "캘린더 위젯 · " + (sample ? "샘플" : connected ? "연결 일정" : "원본 읽기");
                statusText.Text = sample ? "로컬 샘플 · " + CalendarRules.InSeoul(Now).ToString("HH:mm") + " 갱신 · 시작 등록은 별도" : connected ? (cachedOnly ? "저장된 연결 일정 · 온라인 읽기 미실행 · 최신성 미확인" : "Google 읽기 · 상태 빈칸/미연결은 원본 그대로 표시") : "원본 읽기 · Google 쓰기 미실행 · 시작 등록은 별도";
            }
            catch
            {
                loading = false; failed = true;
                if (Snapshot == null)
                {
                    eventList.ItemsSource = new List<EventView>(); emptyPanel.Visibility = Visibility.Visible;
                    emptyText.Text = "샘플 데이터를 읽을 수 없습니다. sample-data.json 형식과 파일을 확인하세요.";
                }
                statusText.Text = Snapshot == null ? "데이터 오류 · 표시할 일정 없음" : "갱신 실패 · 이전 샘플을 표시 중 (최신 아님)";
            }
        }
        public void Render()
        {
            var seoul = CalendarRules.InSeoul(Now);
            clockText.Text = seoul.ToString("yyyy.MM.dd (ddd) HH:mm", CultureInfo.GetCultureInfo("ko-KR")) + " · Asia/Seoul";
            if (Snapshot == null) return;
            var visible = CalendarSelection.Visible(Snapshot, Configuration.VisibleCalendarKeys);
            var selected = calendarSelect.SelectedItem as CalendarProfile;
            string id = selected == null ? "" : selected.Id;
            string calendarId = String.IsNullOrEmpty(id) || !visible.Calendars.Any(c => c.Id == id) ? null : id;
            monthTitle.Text = displayedMonth.ToString("yyyy년 M월", CultureInfo.GetCultureInfo("ko-KR"));
            monthDays.ItemsSource = MonthCalendar.Build(visible, calendarId, displayedMonth, seoul.Date, SelectedDate, ThemeManager.IsDark(Configuration));
            dayHeading.Text = SelectedDate.HasValue ? SelectedDate.Value.ToString("M월 d일 (ddd) 일정", CultureInfo.GetCultureInfo("ko-KR")) : "다가오는 일정";
            var views = SelectedDate.HasValue ? CalendarRules.ProjectDate(visible, calendarId, SelectedDate.Value, Now) : CalendarRules.Project(visible, calendarId, Now);
            foreach (var view in views)
            {
                var profile = Snapshot.Calendars.First(c => view.CalendarId == c.Id);
                List<string> chosen;
                if (Configuration.VisibleFields.TryGetValue(profile.Id, out chosen))
                    view.Fields = view.Fields.Where(f => profile.Fields.Any(def => def.Label == f.Label && chosen.Contains(def.Key))).ToList();
                if (view.StatusContext != null) { var draft = StatusDrafts.Drafts.LastOrDefault(d => d.Key == view.StatusContext.CreateDraft().Key && !d.Superseded); view.PendingStatus = draft == null ? "" : draft.State == "complete" ? "양쪽 저장 확인됨" : draft.Message; }
                if (!String.IsNullOrEmpty(view.StatusConflict)) view.PendingStatus = (String.IsNullOrEmpty(view.PendingStatus) ? "" : view.PendingStatus + " | ") + view.StatusConflict;
            }
            eventList.ItemsSource = views;
            countText.Text = CalendarRules.Cutoff(Now).ToString("yyyy.MM.dd") + " 이후 · " + views.Count + "개 일정 · 종료일 미만";
            emptyPanel.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (SelectedDate.HasValue) countText.Text = SelectedDate.Value.ToString("yyyy.MM.dd") + " · " + views.Count + "개 일정";
            emptyText.Text = Snapshot.Mode == "google-loading" ? "저장된 연결 일정이 없습니다. 연결 캘린더를 불러오고 있습니다." : Snapshot.Mode == "sample" || Snapshot.Mode == "synthetic" ? "오늘 이후 일정이 없습니다. 샘플 일정은 2026년 10월 예시입니다." : "오늘 이후 표시할 일정이 없습니다.";
            if (failed) statusText.Text = "갱신 실패 · 이전 표시 유지 (최신 아님)";
        }
        public void Save()
        {
            if (verification) return;
            try { ConfigurationStore.Save(configPath, Configuration); }
            catch { statusText.Text = "로컬 설정 저장 실패 · 폴더 쓰기 권한을 확인하세요."; }
        }
        private void HandleAppearanceChanged() { if (!disposed) Render(); }
        public void Dispose() { disposed = true; ThemeManager.AppearanceChanged -= HandleAppearanceChanged; connectedReadCancellation.Cancel(); updateCancellation.Cancel(); if (timer != null) timer.Stop(); if (remoteTimer != null) remoteTimer.Stop(); if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } }
    }
}
