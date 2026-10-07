using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CalendarWidget
{
    public sealed class SafeSourceException : Exception { public SafeSourceException(string message) : base(message) { } }
    public sealed partial class SettingsDialog
    {
        public readonly Window Window; private readonly Widget owner; private readonly WidgetConfiguration draft;
        private readonly HttpTransport transport; private readonly SourceWorkflow workflow; private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private AccountConfiguration activeAccount; private CalendarProfile activeCalendar; private bool changing, closed, busy;
        private WorkbookEvents pendingWorkbook; public PreviewDialog LastPreview;
        private string sourceFingerprint;
        private readonly Dictionary<string, string> connectionChecks = new Dictionary<string, string>();
        public Task CurrentAction { get; private set; }
        public Func<WidgetConfiguration, CancellationToken, Task<WorkbookEvents>> SyntheticReader;
        public ICalendarGateway SyntheticGateway;
        public Func<CancellationToken, Task<List<RemoteCalendar>>> SyntheticCalendarReader;
        public Action<string> SyntheticOpenSource;
        public SettingsDialog(Widget owner)
        {
            this.owner = owner; draft = Copy(owner.Configuration); transport = new HttpTransport(); workflow = new SourceWorkflow(owner.Root, transport);
            using (var input = File.OpenRead(Path.Combine(owner.Root, "settings.xaml"))) Window = (Window)XamlReader.Load(input);
            Window.Owner = owner.Window; CurrentAction = Task.FromResult(0);
            ThemeManager.Apply(Window, draft);
            InitializeAppearance();
            InitializeChangeAlerts();
            Text("ReleaseFeed", draft.ReleaseFeed);
            Text("OAuthClientPath", draft.OAuthClientFile); Text("SourceWorkbookPath", draft.SourceWorkbookPath); Text("SourceWorksheet", draft.SourceWorksheet ?? "이벤트확인");
            Text("MicrosoftClientId", draft.MicrosoftClientId); Text("MicrosoftTenant", draft.MicrosoftTenant ?? "organizations");
            var options = draft.ExcelOptions ?? new ExcelReadOptions(); draft.ExcelOptions = options;
            Text("ColumnSettings", options.BaseYear + "," + options.DateColumn + "," + options.PhaseColumn + "," + options.TitleColumn + "," + options.RealTitleColumn);
            Text("PhaseMinutes", options.PhaseDurationMinutes.ToString()); Text("SummaryPrefix", options.SummaryPrefix);
            Find<ComboBox>("SourceKindSelect").SelectedIndex = draft.SourceKind == "local" ? 1 : 0;
            if (workflow.PrivateSources.Exists(draft.SourceDocumentId)) try { Find<PasswordBox>("SourceSharingLink").Password = workflow.PrivateSources.Load(draft.SourceDocumentId); } catch { Status("저장된 공유 링크를 읽지 못했습니다. 다시 입력하세요."); }
            sourceFingerprint = (draft.SourceKind ?? "sharepoint") + "|" + (draft.SourceKind == "local" ? draft.SourceWorkbookPath ?? "" : Find<PasswordBox>("SourceSharingLink").Password) + "|" + draft.SourceWorksheet;
            Find<ComboBox>("AccountSelect").SelectionChanged += delegate { if (!changing) { StoreAccount(); LoadAccount(); } };
            Find<ComboBox>("TargetCalendarSelect").SelectionChanged += delegate { var selected = Find<ComboBox>("TargetCalendarSelect").SelectedItem as RemoteCalendar; if (selected != null) Text("TargetCalendarId", selected.Id); };
            Find<Button>("AddAccountButton").Click += delegate { StoreAccount(); var account = new AccountConfiguration { Id = Guid.NewGuid().ToString("N"), ProviderId = "google", DisplayName = "Google 계정 " + (draft.Accounts.Count + 1), CalendarIds = new List<string>() }; draft.Accounts.Add(account); RefreshAccounts(); RefreshProfileAccounts(); Find<ComboBox>("AccountSelect").SelectedItem = account; };
            Find<Button>("BrowseOAuthClientButton").Click += delegate { Pick("JSON (*.json)|*.json", "Google 데스크톱 클라이언트 설정 선택", "OAuthClientPath"); };
            Find<Button>("BrowseWorkbookButton").Click += delegate { Pick("Excel (*.xlsx)|*.xlsx", "공식 다운로드한 최신 브라운팜 원본 선택", "SourceWorkbookPath"); Find<ComboBox>("SourceKindSelect").SelectedIndex = 1; };
            Find<Button>("OpenSourceButton").Click += delegate { OpenSource(); };
            Find<Button>("ConnectGoogleButton").Click += delegate { Start(ConnectOrVerifyGoogle); };
            Find<Button>("ConnectMicrosoftButton").Click += delegate { Start(ConnectMicrosoft); };
            Find<Button>("LoadCalendarsButton").Click += delegate { Start(LoadCalendars); };
            Find<Button>("ReadSourceButton").Click += delegate { Start(ReadPreview); };
            Find<Button>("SyncButton").Click += delegate { Start(ReadPreview); };
            Find<Button>("LoadSourceCalendarsButton").Click += delegate { Start(LoadSourceTargets); };
            Find<ComboBox>("SourceAccountSelect").SelectionChanged += delegate { Find<ComboBox>("SourceCalendarSelect").ItemsSource = null; InvalidateSourcePreview(); };
            Find<ComboBox>("SourceCalendarSelect").SelectionChanged += delegate { InvalidateSourcePreview(); };
            Find<Button>("CancelSettingsButton").Click += delegate { Window.Close(); };
            Find<Button>("SaveSettingsButton").Click += delegate { Start(SaveSettings); };
            Find<ComboBox>("FieldCalendarSelect").SelectionChanged += delegate { StoreFields(); LoadFields(); };
            Window.Closed += delegate { closed = true; cancellation.Cancel(); Find<PasswordBox>("SourceSharingLink").Clear(); if (!busy) transport.Dispose(); };
            RefreshAccounts(); if (draft.Accounts.Count > 0) Find<ComboBox>("AccountSelect").SelectedIndex = 0; else LoadAccount();
            Find<ComboBox>("SourceAccountSelect").ItemsSource = draft.Accounts;
            Find<ComboBox>("SourceAccountSelect").SelectedItem = draft.Accounts.FirstOrDefault(a => a.Id == draft.DisplayAccountId) ?? draft.Accounts.FirstOrDefault();
            Find<ComboBox>("FieldCalendarSelect").ItemsSource = owner.Snapshot == null ? new List<CalendarProfile>() : owner.Snapshot.Calendars;
            Find<ComboBox>("FieldCalendarSelect").SelectedValue = owner.Configuration.SelectedProfileId;
            if (Find<ComboBox>("FieldCalendarSelect").SelectedIndex < 0 && Find<ComboBox>("FieldCalendarSelect").Items.Count > 0) Find<ComboBox>("FieldCalendarSelect").SelectedIndex = 0;
            InitializeProfiles();
            Window.Loaded += delegate { if (!owner.IsVerification && HasStoredGrant(activeAccount)) Start(LoadCalendars); };
        }
        public T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private void InitializeChangeAlerts()
        {
            var enabled = Find<CheckBox>("ChangeAlertsEnabled"); enabled.IsChecked = draft.ChangeAlertsEnabled;
            enabled.Checked += delegate { draft.ChangeAlertsEnabled = true; };
            enabled.Unchecked += delegate { draft.ChangeAlertsEnabled = false; };
            Find<Button>("PauseChangeAlertsButton").Click += delegate {
                DateTimeOffset until;
                if (DateTimeOffset.TryParse(draft.ChangeAlertsPausedUntilUtc, out until) && until > DateTimeOffset.UtcNow) draft.ChangeAlertsPausedUntilUtc = null;
                else draft.ChangeAlertsPausedUntilUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
                UpdateChangePauseLabel();
            };
            UpdateChangePauseLabel();
        }
        private void UpdateChangePauseLabel()
        {
            DateTimeOffset until;
            bool paused = DateTimeOffset.TryParse(draft.ChangeAlertsPausedUntilUtc, out until) && until > DateTimeOffset.UtcNow;
            Find<Button>("PauseChangeAlertsButton").Content = paused ? "알림 다시 켜기" : "알림 1시간 일시중지";
            Find<TextBlock>("ChangeAlertsPauseInfo").Text = paused ? "일시중지 종료: " + CalendarRules.InSeoul(until).ToString("yyyy.MM.dd HH:mm") + " (한국 시간)" : "Google 조회는 계속하고 변경 알림만 조절합니다.";
        }
        private void InitializeAppearance()
        {
            var mode = Find<ComboBox>("ThemeSelect");
            foreach (var item in mode.Items.OfType<ComboBoxItem>()) if ((string)item.Tag == draft.ThemeMode) mode.SelectedItem = item;
            var fonts = Find<ComboBox>("FontSelect"); fonts.ItemsSource = ThemeManager.InstalledFonts();
            fonts.SelectedItem = draft.FontFamilyName;
            Find<Slider>("FontSizeSelect").Value = draft.DisplayFontSize;
            Find<TextBlock>("FontSizeLabel").Text = draft.DisplayFontSize.ToString("0") + " DIP";
            mode.SelectionChanged += delegate { UpdateAppearance(); };
            fonts.SelectionChanged += delegate { UpdateAppearance(); };
            Find<Slider>("FontSizeSelect").ValueChanged += delegate { UpdateAppearance(); };
            Find<Button>("ResetAppearanceButton").Click += delegate {
                foreach (var item in mode.Items.OfType<ComboBoxItem>()) if ((string)item.Tag == "light") mode.SelectedItem = item;
                fonts.SelectedItem = ThemeManager.DefaultFont;
                Find<Slider>("FontSizeSelect").Value = ThemeManager.DefaultSize;
                UpdateAppearance();
            };
        }
        private void UpdateAppearance()
        {
            var mode = Find<ComboBox>("ThemeSelect").SelectedItem as ComboBoxItem;
            if (mode == null) return;
            draft.ThemeMode = (string)mode.Tag;
            draft.FontFamilyName = Find<ComboBox>("FontSelect").SelectedItem as string ?? ThemeManager.DefaultFont;
            draft.DisplayFontSize = Math.Round(Find<Slider>("FontSizeSelect").Value);
            Find<TextBlock>("FontSizeLabel").Text = draft.DisplayFontSize.ToString("0") + " DIP";
            ThemeManager.Apply(Window, draft);
        }
        private string Text(string name) { return Find<TextBox>(name).Text.Trim(); }
        private void Text(string name, string value) { Find<TextBox>(name).Text = value ?? ""; }
        private void Status(string text) { if (!closed) Find<TextBlock>("SettingsStatus").Text = text; }
        public void OpenSource()
        {
            try {
                string link = Find<PasswordBox>("SourceSharingLink").Password; SharePointReader.ContentPath(link);
                if (owner.IsVerification) { if (SyntheticOpenSource == null) throw new InvalidOperationException("검증에서는 브라우저를 열지 않습니다."); SyntheticOpenSource(link); }
                else Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                Status("공식 페이지에서 최신 xlsx를 다운로드한 뒤 아래에서 선택하세요. Microsoft 앱 로그인 없이 사용합니다.");
            } catch (Exception ex) { Status(SafeError(ex)); }
        }
        private static WidgetConfiguration Copy(WidgetConfiguration value) { return new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<WidgetConfiguration>(JsonData.Serialize(value)); }
        private void Pick(string filter, string title, string field) { var picker = new Microsoft.Win32.OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true }; if (picker.ShowDialog(Window) == true) Text(field, picker.FileName); }
        private void Start(Func<Task> work) { if (!busy) CurrentAction = Run(work); }
        private async Task Run(Func<Task> work)
        {
            busy = true; Find<TabControl>("ConfigTabs").IsEnabled = false; Find<Button>("SaveSettingsButton").IsEnabled = false; Status("작업 중… 취소나 닫기로 중단할 수 있습니다.");
            try { await work(); }
            catch (SafeSourceException ex) { Status(ex.Message); }
            catch (OperationCanceledException) { Status("작업을 취소했습니다."); }
            catch (Exception ex) { Status(SafeError(ex)); }
            finally { busy = false; if (!closed) { Find<TabControl>("ConfigTabs").IsEnabled = true; Find<Button>("SaveSettingsButton").IsEnabled = true; } else transport.Dispose(); }
        }
        public static string SafeError(Exception error) { return error is OAuthExchangeException ? error.Message : error is CalendarApiException ? "API 작업 실패 (응답 " + ((CalendarApiException)error).Status + "). 권한과 연결을 확인하세요." : error is System.Security.Cryptography.CryptographicException ? "PC 사용자 암호화를 사용할 수 없어 중단했습니다. 일반 사용자 세션에서 실행하세요." : "작업을 완료하지 못했습니다. 원본·클라이언트 설정·계정 권한을 확인하세요. 비공개 링크와 인증 응답은 표시하지 않습니다."; }
        private void RequireApproval() { if (!RuntimePermissions.OAuthEnabled) throw new InvalidOperationException("OAuth 실행 승인 대기"); }
        private bool ConfirmConsent(string provider) { return MessageBox.Show(Window, provider + " 로그인과 권한 동의를 시작하고 승인한 지속 접근 정보를 이 PC 사용자 암호화로 저장합니다. 진행하시겠습니까?", "새 연결 동의", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes; }
        private bool HasStoredGrant(AccountConfiguration account)
        {
            return account != null && !String.IsNullOrEmpty(account.GoogleSubject) &&
                (workflow.Credentials.Exists("google-" + account.Id) || (owner.IsVerification && SyntheticCalendarReader != null));
        }
        private void ShowConnectionState()
        {
            string state = null;
            if (activeAccount != null) connectionChecks.TryGetValue(activeAccount.Id, out state);
            var label = Find<TextBlock>("ConnectionStatus");
            var button = Find<Button>("ConnectGoogleButton");
            if (activeAccount == null) { label.Text = "Google 계정을 선택하세요."; button.Content = "Google 계정 연결"; }
            else if (state == "verified") { label.Text = "기존 Google 연결 · 캘린더 읽기 확인됨"; button.Content = "기존 Google 연결 다시 확인"; }
            else if (state == "reauth") { label.Text = "Google 재연결 필요 · 기존 승인 만료 또는 철회"; button.Content = "Google 재연결 시작 (로그인)"; }
            else if (state == "error") { label.Text = "Google 읽기 확인 실패 · 연결 상태 미확인"; button.Content = "기존 Google 연결 다시 확인"; }
            else if (HasStoredGrant(activeAccount)) { label.Text = "저장된 Google 연결 정보 있음 · 읽기 확인 전"; button.Content = "기존 Google 연결 확인"; }
            else { label.Text = "저장된 Google 연결 없음 · 새 로그인 필요"; button.Content = "Google 계정 연결 (새 로그인)"; }
        }
        private async Task ConnectOrVerifyGoogle()
        {
            StoreAccount();
            if (activeAccount == null) { Status("Google 계정을 선택하세요."); return; }
            string state = null; connectionChecks.TryGetValue(activeAccount.Id, out state);
            if (HasStoredGrant(activeAccount) && state != "reauth") { await LoadCalendars(); return; }
            if (!RuntimePermissions.OAuthEnabled) { Status("이 실행 모드에서는 새 Google 로그인을 시작할 수 없습니다. 기존 연결 확인은 저장된 계정에서 사용할 수 있습니다."); return; }
            await ConnectGoogle();
        }
        private async Task ConnectGoogle()
        {
            if (!RuntimePermissions.OAuthEnabled) { Status("이 실행 모드에서는 새 Google 로그인을 시작할 수 없습니다. 기존 연결 확인을 사용하세요."); return; }
            ApplyDraft(); if (activeAccount == null) throw new InvalidOperationException("계정을 선택하세요.");
            OAuthToken token;
            try { token = await workflow.Auth.Connect(OAuthClientSettings.Google(draft.OAuthClientFile), "google-" + activeAccount.Id, activeAccount.Email, true, cancellation.Token); }
            catch (OAuthExchangeException error) {
                try { Directory.CreateDirectory(Path.Combine(owner.Root, "evidence")); File.WriteAllText(Path.Combine(owner.Root, "evidence/google-oauth-error.json"), JsonData.Serialize(new { provider = "google", httpStatus = error.HttpStatus, error = error.ErrorCode, error_description = error.SafeDescription, reportedAtUtc = DateTimeOffset.UtcNow.ToString("o") })); } catch { }
                throw;
            }
            if (closed) return; activeAccount.GoogleSubject = token.Subject; activeAccount.VerifiedEmail = token.VerifiedEmail; activeAccount.Email = token.VerifiedEmail; Text("AccountEmail", token.VerifiedEmail);
            PersistConnection(); await LoadCalendars();
        }
        private async Task ConnectMicrosoft()
        {
            if (!RuntimePermissions.OAuthEnabled) { Status("Microsoft 실제 로그인·지속 저장 실행은 승인 대기입니다. SharePoint 링크는 인증을 대신하지 않습니다."); return; }
            ApplyDraft(); if (!ConfirmConsent("Microsoft Files.Read")) { Status("연결을 취소했습니다."); return; }
            await workflow.Auth.Connect(OAuthClientSettings.Microsoft(draft.MicrosoftClientId, draft.MicrosoftTenant), "microsoft-source", "", true, cancellation.Token);
            if (!closed) Status("Microsoft 문서 읽기 연결을 저장했습니다. 원본 읽기로 실제 접근을 확인하세요.");
        }
        private void PersistConnection()
        {
            var existing = owner.Configuration.Accounts.FindIndex(a => a.Id == activeAccount.Id);
            var copy = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AccountConfiguration>(JsonData.Serialize(activeAccount));
            if (existing < 0) owner.Configuration.Accounts.Add(copy); else owner.Configuration.Accounts[existing] = copy;
            owner.Configuration.OAuthClientFile = draft.OAuthClientFile; owner.Save(); LoadAccount();
        }
        private async Task LoadCalendars()
        {
            bool mock = owner.IsVerification && SyntheticCalendarReader != null;
            ApplyDraft();
            if (activeAccount == null) { Status("Google 계정을 선택하세요."); return; }
            if (!mock && !HasStoredGrant(activeAccount)) { ShowConnectionState(); Status("저장된 Google 연결이 없습니다. 새 로그인은 사용자가 직접 시작해야 합니다."); return; }
            try {
                var calendars = mock ? await SyntheticCalendarReader(cancellation.Token) : await workflow.GoogleExisting(draft, activeAccount).Calendars(cancellation.Token);
                if (closed) return;
                connectionChecks[activeAccount.Id] = "verified"; ShowConnectionState();
                var owned = calendars.Where(CalendarAccess.CanWrite).ToList();
                Find<ComboBox>("TargetCalendarSelect").ItemsSource = owned;
                Find<ComboBox>("TargetCalendarSelect").SelectedValue = activeAccount.TargetCalendarId;
                Status(!String.IsNullOrEmpty(activeAccount.TargetCalendarId) && !owned.Any(c => c.Id == activeAccount.TargetCalendarId)
                    ? "Google 캘린더 목록 읽기는 확인됐지만 선택된 대상 캘린더의 소유 권한을 확인하지 못했습니다."
                    : "기존 Google 연결로 소유한 캘린더 목록을 실제로 읽어 확인했습니다.");
            }
            catch (OperationCanceledException) { throw; }
            catch (OAuthExchangeException error) {
                if (closed) return;
                connectionChecks[activeAccount.Id] = error.ErrorCode == "invalid_grant" ? "reauth" : "error"; ShowConnectionState();
                Status(error.ErrorCode == "invalid_grant" ? "기존 Google 승인이 만료되거나 철회됐습니다. 재연결이 필요합니다. 새 로그인은 자동으로 시작하지 않습니다." : "기존 Google 토큰 갱신에 실패했습니다. 연결 상태를 확인하세요.");
            }
            catch (CalendarApiException error) {
                if (closed) return;
                connectionChecks[activeAccount.Id] = error.Status == 401 ? "reauth" : "error"; ShowConnectionState();
                Status(error.Status == 401 ? "기존 Google 연결을 다시 확인해야 합니다. 새 로그인은 자동으로 시작하지 않습니다." : "Google 캘린더 목록 읽기에 실패했습니다. 권한 또는 네트워크를 확인하세요.");
            }
            catch {
                if (closed) return;
                connectionChecks[activeAccount.Id] = "error"; ShowConnectionState();
                Status("Google 읽기 확인에 실패했습니다. 저장된 연결 정보와 네트워크를 확인하세요.");
            }
        }
        private void InvalidateSourcePreview()
        {
            if (LastPreview != null && LastPreview.Window.IsVisible) return;
            LastPreview = null; pendingWorkbook = null; Find<Button>("SyncButton").IsEnabled = false;
        }
        private async Task LoadSourceTargets()
        {
            StoreAccount(); draft.OAuthClientFile = Text("OAuthClientPath");
            var account = Find<ComboBox>("SourceAccountSelect").SelectedItem as AccountConfiguration;
            if (account == null || String.IsNullOrWhiteSpace(account.GoogleSubject)) throw new SafeSourceException("동기화할 연결 계정을 선택하세요.");
            var calendars = owner.IsVerification && SyntheticCalendarReader != null ? await SyntheticCalendarReader(cancellation.Token) :
                await workflow.GoogleExisting(draft, account).Calendars(cancellation.Token);
            if (closed) return;
            var writable = calendars.Where(CalendarAccess.CanWrite).ToList();
            Find<ComboBox>("SourceCalendarSelect").ItemsSource = writable;
            Find<ComboBox>("SourceCalendarSelect").SelectedValue = account.TargetCalendarId;
            Status(writable.Count == 0 ? "현재 계정에서 쓰기 가능한 캘린더가 확인되지 않았습니다." : "동기화할 캘린더 한 곳을 선택하세요.");
        }
        public async Task ReadPreview()
        {
            if (LastPreview != null && LastPreview.Window.IsVisible) { LastPreview.Window.Activate(); Status("열려 있는 변경 미리보기를 확인하세요."); return; }
            ApplyDraft(); var captured = Copy(draft);
            var chosenAccount = Find<ComboBox>("SourceAccountSelect").SelectedItem as AccountConfiguration;
            var capturedAccount = chosenAccount == null ? null : new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<AccountConfiguration>(JsonData.Serialize(chosenAccount));
            var chosenCalendar = Find<ComboBox>("SourceCalendarSelect").SelectedItem as RemoteCalendar;
            string targetId = chosenCalendar == null ? (owner.IsVerification && capturedAccount != null ? capturedAccount.TargetCalendarId : null) : chosenCalendar.Id;
            if (!owner.IsVerification && String.IsNullOrWhiteSpace(targetId)) throw new SafeSourceException("미리보기 전에 동기화할 계정과 캘린더를 선택하세요.");
            if (captured.SourceKind != "local") throw new SafeSourceException("현재 원본 읽기는 다운로드한 XLSX 파일을 선택해야 합니다.");
            if (!(owner.IsVerification && SyntheticReader != null) && (String.IsNullOrWhiteSpace(captured.SourceWorkbookPath) || !File.Exists(captured.SourceWorkbookPath))) throw new SafeSourceException("선택한 XLSX 파일을 찾지 못했습니다. 다운로드 경로를 확인하세요.");
            captured.ExcelOptions.AllowUnlinkedSortedRows = true;
            string link = Find<PasswordBox>("SourceSharingLink").Password;
            WorkbookEvents workbook;
            try {
                if (!(owner.IsVerification && SyntheticReader != null) && !XlsxReader.HasSheet(captured.SourceWorkbookPath, captured.SourceWorksheet)) throw new SafeSourceException("선택한 상태 시트를 XLSX에서 찾지 못했습니다. 상태 시트 이름을 확인하세요.");
                if (owner.IsVerification && SyntheticReader != null) workbook = await SyntheticReader(captured, cancellation.Token); else workbook = await workflow.Read(captured, link, cancellation.Token);
                if (workbook.Events.Count == 0) throw new SafeSourceException("선택한 XLSX에서 일정이 0건입니다. 일정 시트와 상태 시트를 구분해 확인하세요.");
            } catch (IOException) { throw new SafeSourceException("XLSX 파일에 접근하지 못했습니다. 파일 잠금 또는 다운로드 상태를 확인하세요."); }
            catch (InvalidDataException) { throw new SafeSourceException("XLSX 일정 구조 검증에 실패했습니다. 이벤트 정렬 시트, 원본 셀 연결, 상태 시트를 확인하세요."); }
            if (closed) return;
            var repo = new SyncRepository(Path.Combine(owner.Root, owner.IsVerification ? "evidence/" + owner.VerificationStateFile : ".state/sync-state.json")); repo.ResolveIdentities(workbook); pendingWorkbook = workbook;
            ICalendarGateway gateway = null; List<RemoteEvent> remote = null;
            if (owner.IsVerification && SyntheticGateway != null) { gateway = SyntheticGateway; remote = await gateway.List(targetId ?? "", owner.Now, cancellation.Token); }
            else if (capturedAccount != null && !String.IsNullOrEmpty(capturedAccount.GoogleSubject) && !String.IsNullOrEmpty(targetId))
            {
                var google = workflow.GoogleExisting(captured, capturedAccount); gateway = google;
                remote = await workflow.Inspect(google, repo, capturedAccount.GoogleSubject ?? capturedAccount.Id, targetId, captured.SourceDocumentId, owner.Now, cancellation.Token);
            }
            if (closed) return;
            var engine = new SyncEngine(repo); var plan = engine.Plan(workbook, capturedAccount == null ? "unselected" : capturedAccount.GoogleSubject ?? capturedAccount.Id, targetId ?? "", remote, captured.GoogleColorId, owner.Now);
            plan.AccountEmail = capturedAccount == null ? "" : capturedAccount.Email;
            Func<CancellationToken, Task<WorkbookEvents>> reread = token => owner.IsVerification && SyntheticReader != null ? SyntheticReader(captured, token) : workflow.Read(captured, link, token);
            LastPreview = new PreviewDialog(Window, owner.Root, repo, engine, plan, gateway, reread, owner.IsVerification, owner.Now, true);
            var openedPreview = LastPreview;
            Find<ComboBox>("SourceAccountSelect").IsEnabled = false; Find<ComboBox>("SourceCalendarSelect").IsEnabled = false;
            openedPreview.Window.Closed += delegate { if (!closed) { Find<ComboBox>("SourceAccountSelect").IsEnabled = true; Find<ComboBox>("SourceCalendarSelect").IsEnabled = true; } };
            LastPreview.Window.Show(); Find<Button>("SyncButton").IsEnabled = true;
            Status("원본 " + workbook.Events.Count + "건을 읽었습니다. 변경 미리보기에서 확인하세요. Google 쓰기는 실행하지 않았습니다.");
        }
        private void ApplyDraft()
        {
            StoreAccount(); StoreFields(); draft.OAuthClientFile = Text("OAuthClientPath"); draft.SourceWorkbookPath = Text("SourceWorkbookPath"); draft.SourceWorksheet = Text("SourceWorksheet");
            draft.MicrosoftClientId = Text("MicrosoftClientId"); draft.MicrosoftTenant = Text("MicrosoftTenant"); draft.SourceKind = (string)((ComboBoxItem)Find<ComboBox>("SourceKindSelect").SelectedItem).Tag;
            var columns = Text("ColumnSettings").Split(',').Select(s => s.Trim().ToUpperInvariant()).ToArray(); int year, minutes;
            if (columns.Length != 5 || !Int32.TryParse(columns[0], out year) || year < 2000 || year > 2100 || columns.Skip(1).Any(c => !System.Text.RegularExpressions.Regex.IsMatch(c, "^[A-Z]{1,3}$")) || !Int32.TryParse(Text("PhaseMinutes"), out minutes) || minutes < 0 || minutes > 1440) throw new InvalidOperationException("원본 열/연도/단계 길이 설정 오류");
            draft.ExcelOptions.BaseYear = year; draft.ExcelOptions.DateColumn = columns[1]; draft.ExcelOptions.PhaseColumn = columns[2]; draft.ExcelOptions.TitleColumn = columns[3]; draft.ExcelOptions.RealTitleColumn = columns[4]; draft.ExcelOptions.PhaseDurationMinutes = minutes; draft.ExcelOptions.SummaryPrefix = Text("SummaryPrefix");
            if (draft.Accounts.Any(a => !String.IsNullOrEmpty(a.Email) && !System.Text.RegularExpressions.Regex.IsMatch(a.Email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))) throw new InvalidOperationException("이메일 형식 오류");
            if (!String.IsNullOrEmpty(Find<PasswordBox>("SourceSharingLink").Password)) SharePointReader.ContentPath(Find<PasswordBox>("SourceSharingLink").Password);
            string newFingerprint = draft.SourceKind + "|" + (draft.SourceKind == "local" ? draft.SourceWorkbookPath ?? "" : Find<PasswordBox>("SourceSharingLink").Password) + "|" + draft.SourceWorksheet;
            if (newFingerprint != sourceFingerprint) {
                draft.SourceDocumentId = Guid.NewGuid().ToString("N"); sourceFingerprint = newFingerprint; pendingWorkbook = null;
                if (draft.Connections != null) foreach (var profile in draft.Connections.Where(p => p.Id.StartsWith("legacy-", StringComparison.Ordinal) && p.Mode == "excel")) {
                    profile.WorkbookPath = draft.SourceWorkbookPath; profile.Worksheet = draft.SourceWorksheet;
                    profile.DocumentId = draft.SourceDocumentId; profile.ExcelOptions = Copy(draft).ExcelOptions;
                }
            }
        }
        private async Task SaveSettings()
        {
            try
            {
                StoreProfile(); ApplyDraft(); ConnectionProfiles.Validate(draft, draft.Connections); string link = Find<PasswordBox>("SourceSharingLink").Password;
                if (!String.IsNullOrEmpty(link)) workflow.PrivateSources.Save(draft.SourceDocumentId, link);
                bool sameSource = draft.SourceDocumentId == owner.Configuration.SourceDocumentId;
                owner.Configuration.Accounts = draft.Accounts; owner.Configuration.VisibleFields = draft.VisibleFields;
                owner.Configuration.ThemeMode = draft.ThemeMode; owner.Configuration.FontFamilyName = draft.FontFamilyName; owner.Configuration.DisplayFontSize = draft.DisplayFontSize;
                owner.Configuration.ChangeAlertsEnabled = draft.ChangeAlertsEnabled; owner.Configuration.ChangeAlertsPausedUntilUtc = draft.ChangeAlertsPausedUntilUtc;
                owner.Configuration.ReleaseFeed = Text("ReleaseFeed");
                ThemeManager.Apply(owner.Window, owner.Configuration);
                owner.Configuration.Connections = draft.Connections;
                if (draft.Connections.Any(p => p.Mode == "google-direct")) owner.Configuration.UseConnectedCalendar = true;
                owner.Configuration.SourceKind = draft.SourceKind; owner.Configuration.SourceWorkbookPath = draft.SourceWorkbookPath; owner.Configuration.SourceWorksheet = draft.SourceWorksheet; owner.Configuration.SourceDocumentId = draft.SourceDocumentId; owner.Configuration.ExcelOptions = draft.ExcelOptions;
                owner.Configuration.OAuthClientFile = draft.OAuthClientFile; owner.Configuration.MicrosoftClientId = draft.MicrosoftClientId; owner.Configuration.MicrosoftTenant = draft.MicrosoftTenant;
                owner.Configuration.UseWorkbook = pendingWorkbook != null || (sameSource && owner.Configuration.UseWorkbook);
                if (!sameSource && !draft.Connections.Any(p => p.Mode == "google-direct")) owner.Configuration.UseConnectedCalendar = false;
                else if (owner.Configuration.UseConnectedCalendar && activeAccount != null && !String.IsNullOrEmpty(activeAccount.GoogleSubject)) owner.Configuration.DisplayAccountId = activeAccount.Id;
                owner.Save(); if (owner.Configuration.UseConnectedCalendar) { await owner.RefreshSource(); } else if (pendingWorkbook != null) owner.DisplayWorkbook(pendingWorkbook, owner.IsVerification ? "synthetic" : draft.SourceKind == "sharepoint" ? "sharepoint-snapshot" : "local-xlsx"); else if (!sameSource) owner.ResetToSample(); else owner.Render(); Window.Close();
            }
            catch (Exception ex) { Status(SafeError(ex)); }
        }
        private void RefreshAccounts() { changing = true; var select = Find<ComboBox>("AccountSelect"); select.ItemsSource = null; select.ItemsSource = draft.Accounts; changing = false; }
        private void LoadAccount()
        {
            activeAccount = Find<ComboBox>("AccountSelect").SelectedItem as AccountConfiguration;
            foreach (var field in new[] { "AccountName", "AccountEmail", "AccountCalendars", "TargetCalendarId" }) Find<TextBox>(field).IsEnabled = activeAccount != null;
            Text("AccountName", activeAccount == null ? "" : activeAccount.DisplayName); Text("AccountEmail", activeAccount == null ? "" : activeAccount.Email);
            Text("AccountCalendars", activeAccount == null || activeAccount.CalendarIds == null ? "" : String.Join(", ", activeAccount.CalendarIds)); Text("TargetCalendarId", activeAccount == null ? "" : activeAccount.TargetCalendarId);
            ShowConnectionState();
            Find<ComboBox>("TargetCalendarSelect").ItemsSource = null;
        }
        private void StoreAccount()
        {
            if (activeAccount == null) return; activeAccount.DisplayName = Text("AccountName"); string updatedEmail = Text("AccountEmail");
            if (!String.IsNullOrEmpty(activeAccount.VerifiedEmail) && !String.Equals(activeAccount.VerifiedEmail, updatedEmail, StringComparison.OrdinalIgnoreCase)) { activeAccount.GoogleSubject = null; activeAccount.VerifiedEmail = null; }
            activeAccount.Email = updatedEmail; activeAccount.CalendarIds = Text("AccountCalendars").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList(); activeAccount.TargetCalendarId = Text("TargetCalendarId");
        }
        private void StoreFields() { if (activeCalendar != null) draft.VisibleFields[activeCalendar.Id] = Find<StackPanel>("FieldChoices").Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList(); }
        private void LoadFields()
        {
            activeCalendar = Find<ComboBox>("FieldCalendarSelect").SelectedItem as CalendarProfile; var choices = Find<StackPanel>("FieldChoices"); choices.Children.Clear(); if (activeCalendar == null) return;
            List<string> selected; draft.VisibleFields.TryGetValue(activeCalendar.Id, out selected);
            foreach (var field in activeCalendar.Fields) choices.Children.Add(new CheckBox { Content = field.Label, Tag = field.Key, IsChecked = selected == null || selected.Contains(field.Key), Margin = new Thickness(0, 0, 0, 8) });
        }
    }
    public sealed class PreviewDialog
    {
        public readonly Window Window; public SyncPlan Plan { get; private set; } public Task CurrentAction { get; private set; }
        private readonly SyncRepository repo; private readonly SyncEngine engine; private readonly ICalendarGateway gateway; private readonly Func<CancellationToken, Task<WorkbookEvents>> reread;
        private readonly string root;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource(); private readonly bool synthetic, allowExistingGrantWrite; private readonly DateTimeOffset now; private List<RemoteEvent> remote;
        private bool busy, closed;
        public PreviewDialog(Window owner, string root, SyncRepository repo, SyncEngine engine, SyncPlan plan, ICalendarGateway gateway, Func<CancellationToken, Task<WorkbookEvents>> reread, bool synthetic, DateTimeOffset now, bool allowExistingGrantWrite = false)
        {
            this.repo = repo; this.engine = engine; Plan = plan; this.gateway = gateway; this.reread = reread; this.synthetic = synthetic; this.allowExistingGrantWrite = allowExistingGrantWrite; this.now = now; this.root = root; CurrentAction = Task.FromResult(0);
            remote = plan.RemoteEvents;
            using (var input = File.OpenRead(Path.Combine(root, "preview.xaml"))) Window = (Window)XamlReader.Load(input); Window.Owner = owner;
            ThemeManager.ApplyFromOwner(Window, owner);
            Find<Button>("BackButton").Click += delegate { Window.Close(); };
            Find<CheckBox>("ReviewWarningsCheck").Checked += delegate { UpdateExecution(); }; Find<CheckBox>("ReviewWarningsCheck").Unchecked += delegate { UpdateExecution(); };
            Find<Button>("ExecuteSyncButton").Click += delegate { if (!busy) CurrentAction = Execute(); };
            Find<DataGrid>("PlanGrid").SelectionChanged += delegate { UpdateNewConfirmationCount(); };
            Find<Button>("SelectConfirmableButton").Click += delegate { var grid = Find<DataGrid>("PlanGrid"); grid.SelectedItems.Clear(); foreach (var item in Plan.Items.Where(i => i.NewConfirmationAvailable)) grid.SelectedItems.Add(item); UpdateNewConfirmationCount(); };
            Find<Button>("ClearPlanSelectionButton").Click += delegate { Find<DataGrid>("PlanGrid").SelectedItems.Clear(); UpdateNewConfirmationCount(); };
            Find<Button>("ConfirmNewButton").Click += delegate { ConfirmSelectedNew(); };
            Find<Button>("BindIdentityButton").Click += delegate { try { var selected = Find<DataGrid>("PlanGrid").SelectedItem as SyncPlanItem; if (selected != null && selected.Source != null) { repo.BindIdentity(Plan.Workbook, selected.Source, Find<TextBox>("ExistingSourceId").Text.Trim()); Replan(); } } catch (Exception ex) { Find<TextBlock>("PreviewStatus").Text = SettingsDialog.SafeError(ex); } };
            Find<Button>("ImportMappingButton").Click += delegate { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true, Title = "현재 원본·계정·캘린더에 기존 ID 매핑 연결" }; if (picker.ShowDialog(Window) == true) Import(picker.FileName); };
            Window.Closed += delegate { closed = true; cancellation.Cancel(); };
            Render();
        }
        public T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        public void Import(string path) { try { repo.Import(path, Plan.Scope, Plan.Workbook, Plan.AccountId, Plan.CalendarId); Replan(); } catch (Exception ex) { Find<TextBlock>("PreviewStatus").Text = SettingsDialog.SafeError(ex); } }
        private void Replan()
        {
            string email = Plan.AccountEmail;
            Plan = engine.Plan(Plan.Workbook, Plan.AccountId, Plan.CalendarId, remote, Plan.ColorId, now); Plan.AccountEmail = email;
            Render();
        }
        private void UpdateNewConfirmationCount()
        {
            var grid = Find<DataGrid>("PlanGrid");
            int eligible = Plan.Items.Count(i => i.NewConfirmationAvailable);
            int selected = grid.SelectedItems.Cast<object>().OfType<SyncPlanItem>().Count(i => i.NewConfirmationAvailable);
            int excluded = Plan.Items.Count - eligible;
            Find<TextBlock>("NewConfirmationCount").Text = "신규 확인 가능 " + eligible + "건 · 선택 " + selected + "건 · 제외 " + excluded + "건 (기존 일정 후보, ID 충돌, 원본 오류 등은 개별 검토)";
            Find<Button>("ConfirmNewButton").IsEnabled = !busy && selected > 0;
            Find<Button>("SelectConfirmableButton").IsEnabled = !busy && eligible > 0;
            Find<Button>("ClearPlanSelectionButton").IsEnabled = !busy && grid.SelectedItems.Count > 0;
        }
        private void ConfirmSelectedNew()
        {
            var selected = Find<DataGrid>("PlanGrid").SelectedItems.Cast<object>().OfType<SyncPlanItem>().Where(i => i.NewConfirmationAvailable).ToList();
            if (selected.Count == 0) { Find<TextBlock>("PreviewStatus").Text = "신규 확인 가능한 행을 먼저 선택하세요."; return; }
            int confirmed = 0;
            try
            {
                foreach (var item in selected) { repo.ConfirmNewForTarget(Plan.Workbook, item.Source, Plan.Scope); confirmed++; }
                Replan();
                Find<TextBlock>("PreviewStatus").Text = confirmed + "건의 원본 ID를 이 캘린더에 대해 확인했습니다. 실제 일정 등록은 아직 실행하지 않았습니다.";
            }
            catch (Exception ex)
            {
                Replan(); Find<TextBlock>("PreviewStatus").Text = confirmed + "건 확인 후 중단했습니다. 나머지를 다시 선택해 확인하세요. " + SettingsDialog.SafeError(ex);
            }
        }
        private void Render()
        {
            Find<DataGrid>("PlanGrid").ItemsSource = Plan.Items;
            Find<TextBlock>("PreviewSummary").Text = (synthetic ? "합성 테스트 · 모의 API / " : "원본 읽기 / ") + (Plan.RemoteInspected ? "원격 확인됨" : "오프라인 · 원격 미확인") + " · " + String.Join(" · ", Plan.Items.GroupBy(i => i.Action).Select(g => g.Key + " " + g.Count())) + "\nMeet·참석자·설명을 추가하지 않으며 삭제하지 않습니다.";
            Find<TextBlock>("PreviewWarnings").Text = String.Join("\n", Plan.Workbook.Warnings.Distinct()); UpdateExecution(); UpdateNewConfirmationCount();
        }
        private void UpdateExecution() { Find<Button>("ExecuteSyncButton").IsEnabled = !busy && gateway != null && Plan.RemoteInspected && (synthetic || RuntimePermissions.OAuthEnabled || allowExistingGrantWrite) && Find<CheckBox>("ReviewWarningsCheck").IsChecked == true; }
        private async Task<string> MirrorNewSourceStatuses(SyncPlan executed)
        {
            var google = gateway as GoogleGateway;
            if (google == null || synthetic) return "";
            var status = new GoogleStatusEndpoint(google, executed.AccountId, executed.CalendarId, true);
            var store = new StatusDraftStore(Path.Combine(root, ".state", "initial-status-drafts.json"));
            int complete = 0, held = 0;
            foreach (var item in executed.Items.Where(i => (i.Action == "신규" || i.Action == "채택") && i.Source != null && i.Source.StatusBinding != null)) {
                var mapping = repo.State.Mappings.SingleOrDefault(m => m.Scope == executed.Scope && m.StableId == item.StableId && m.GoogleEventId == item.GoogleId);
                var observed = remote.SingleOrDefault(r => r.Id == item.GoogleId);
                if (mapping == null || observed == null || mapping.LastHash != observed.Hash) { held++; continue; }
                var context = new StatusEditContext { Scope = executed.Scope, AccountId = executed.AccountId, CalendarId = executed.CalendarId,
                    GoogleEventId = item.GoogleId, GoogleVersion = observed.ETag, Binding = item.Source.StatusBinding, GoogleOnly = true };
                var draft = context.CreateDraft();
                var previous = store.Drafts.LastOrDefault(d => d.Key == draft.Key && !d.Superseded && d.State != "complete");
                try {
                    if (previous != null) draft = previous; else store.Put(draft);
                    await new GoogleOnlyStatusSaveCoordinator(store).Save(draft, status, cancellation.Token);
                    if (draft.State == "complete") complete++; else held++;
                } catch { held++; }
            }
            return complete == 0 && held == 0 ? "" : "\n상태 설명 반영 확인 " + complete + "건 · 확인 대기 " + held + "건. 확인 대기 항목은 중복 생성하지 않고 다시 검토하세요.";
        }
        private async Task Execute()
        {
            busy = true; UpdateExecution(); foreach (var name in new[] { "ImportMappingButton", "ConfirmNewButton", "BindIdentityButton", "SelectConfirmableButton", "ClearPlanSelectionButton" }) Find<Button>(name).IsEnabled = false;
            try
            {
                if (!synthetic && !RuntimePermissions.OAuthEnabled && !allowExistingGrantWrite) throw new InvalidOperationException("실제 인증 실행 승인 대기");
                var latest = await reread(cancellation.Token); var results = await engine.Execute(Plan, gateway, latest.FileHash, cancellation.Token);
                if (!closed)
                {
                    var executed = Plan;
                    remote = await gateway.List(Plan.CalendarId, new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)), cancellation.Token);
                    var mirror = await MirrorNewSourceStatuses(executed);
                    Replan(); Find<TextBlock>("PreviewStatus").Text = String.Join("\n", results) + mirror;
                }
            }
            catch (OperationCanceledException) { if (!closed) Find<TextBlock>("PreviewStatus").Text = "동기화를 취소했습니다. 다음 실행에서 지정 ID 상태를 확인합니다."; }
            catch (Exception ex) { if (!closed) Find<TextBlock>("PreviewStatus").Text = SettingsDialog.SafeError(ex); }
            finally { busy = false; if (!closed) { foreach (var name in new[] { "ImportMappingButton", "BindIdentityButton" }) Find<Button>(name).IsEnabled = true; UpdateExecution(); UpdateNewConfirmationCount(); } }
        }
    }
}
