using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CalendarWidget
{
    public sealed partial class SettingsDialog
    {
        private CalendarConnectionProfile activeProfile;
        private bool profileChanging;
        private void InitializeProfiles()
        {
            draft.Connections = ConnectionProfiles.Effective(draft).Select(p =>
                new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<CalendarConnectionProfile>(JsonData.Serialize(p))).ToList();
            Find<ComboBox>("ConnectionProfileSelect").SelectionChanged += delegate { if (!profileChanging) { StoreProfile(); LoadProfile(); } };
            Find<ComboBox>("ProfileAccountSelect").SelectionChanged += delegate { if (!profileChanging) { StoreProfile(); Find<ComboBox>("ProfileCalendarSelect").ItemsSource = null; } };
            Find<ComboBox>("ProfileModeSelect").SelectionChanged += delegate { if (!profileChanging) UpdateProfileMode(); };
            Find<Button>("AddConnectionButton").Click += delegate {
                StoreProfile(); var account = Find<ComboBox>("AccountSelect").SelectedItem as AccountConfiguration;
                var profile = new CalendarConnectionProfile { Id = Guid.NewGuid().ToString("N"), AccountId = account == null ? null : account.Id,
                    Mode = "google-direct", WorkbookPath = draft.SourceWorkbookPath, Worksheet = draft.SourceWorksheet,
                    DocumentId = Guid.NewGuid().ToString("N"), ExcelOptions = Copy(draft).ExcelOptions };
                draft.Connections.Add(profile); RefreshProfiles(); Find<ComboBox>("ConnectionProfileSelect").SelectedItem = profile;
            };
            Find<Button>("LoadProfileCalendarsButton").Click += delegate { Start(LoadProfileCalendars); };
            Find<Button>("BrowseProfileWorkbookButton").Click += delegate { Pick("Excel (*.xlsx)|*.xlsx", "지원하는 Excel 달력 선택", "ProfileWorkbookPath"); };
            Find<Button>("ProfilePreviewButton").Click += delegate { Start(ReadProfilePreview); };
            RefreshProfileAccounts(); RefreshProfiles();
            if (draft.Connections.Count > 0) Find<ComboBox>("ConnectionProfileSelect").SelectedIndex = 0; else LoadProfile();
        }
        private void RefreshProfileAccounts()
        {
            profileChanging = true; Find<ComboBox>("ProfileAccountSelect").ItemsSource = null;
            Find<ComboBox>("ProfileAccountSelect").ItemsSource = draft.Accounts; profileChanging = false;
        }
        private void RefreshProfiles()
        {
            profileChanging = true; Find<ComboBox>("ConnectionProfileSelect").ItemsSource = null;
            Find<ComboBox>("ConnectionProfileSelect").ItemsSource = draft.Connections; profileChanging = false;
        }
        private void StoreProfile()
        {
            if (activeProfile == null || profileChanging) return;
            var account = Find<ComboBox>("ProfileAccountSelect").SelectedItem as AccountConfiguration;
            var calendar = Find<ComboBox>("ProfileCalendarSelect").SelectedItem as RemoteCalendar;
            var mode = Find<ComboBox>("ProfileModeSelect").SelectedItem as ComboBoxItem;
            if (account != null) activeProfile.AccountId = account.Id;
            if (calendar != null) activeProfile.CalendarId = calendar.Id;
            if (mode != null) activeProfile.Mode = (string)mode.Tag;
            activeProfile.DisplayName = Text("ProfileDisplayName");
            activeProfile.WorkbookPath = Text("ProfileWorkbookPath");
            activeProfile.Worksheet = Text("ProfileWorksheet");
        }
        private void LoadProfile()
        {
            activeProfile = Find<ComboBox>("ConnectionProfileSelect").SelectedItem as CalendarConnectionProfile;
            profileChanging = true;
            Find<ComboBox>("ProfileAccountSelect").SelectedItem = activeProfile == null ? null : draft.Accounts.FirstOrDefault(a => a.Id == activeProfile.AccountId);
            Find<ComboBox>("ProfileCalendarSelect").ItemsSource = null;
            Find<ComboBox>("ProfileModeSelect").SelectedIndex = activeProfile == null ? -1 : activeProfile.Mode == "excel" ? 1 : 0;
            Text("ProfileDisplayName", activeProfile == null ? "" : activeProfile.DisplayName);
            Text("ProfileWorkbookPath", activeProfile == null ? "" : activeProfile.WorkbookPath);
            Text("ProfileWorksheet", activeProfile == null ? "" : activeProfile.Worksheet);
            profileChanging = false; UpdateProfileMode();
        }
        private void UpdateProfileMode()
        {
            var mode = Find<ComboBox>("ProfileModeSelect").SelectedItem as ComboBoxItem;
            bool excel = mode != null && (string)mode.Tag == "excel";
            Find<StackPanel>("ProfileExcelFields").Visibility = excel ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("ProfilePreviewButton").IsEnabled = excel;
        }
        private async Task LoadProfileCalendars()
        {
            StoreAccount(); StoreProfile(); draft.OAuthClientFile = Text("OAuthClientPath");
            var account = Find<ComboBox>("ProfileAccountSelect").SelectedItem as AccountConfiguration;
            if (account == null || String.IsNullOrEmpty(account.GoogleSubject)) throw new InvalidOperationException("연결된 Google 계정을 선택하세요.");
            var calendars = owner.IsVerification && SyntheticCalendarReader != null ? await SyntheticCalendarReader(cancellation.Token) :
                await workflow.GoogleExisting(draft, account).Calendars(cancellation.Token);
            if (closed) return;
            var mode = Find<ComboBox>("ProfileModeSelect").SelectedItem as ComboBoxItem;
            bool excel = mode != null && (string)mode.Tag == "excel";
            var choices = calendars.Where(c => excel ? CalendarAccess.CanWrite(c) : CalendarAccess.CanRead(c)).ToList();
            Find<ComboBox>("ProfileCalendarSelect").ItemsSource = choices;
            Find<ComboBox>("ProfileCalendarSelect").SelectedValue = activeProfile == null ? null : activeProfile.CalendarId;
            Status(activeProfile != null && !String.IsNullOrEmpty(activeProfile.CalendarId) && !choices.Any(c => c.Id == activeProfile.CalendarId)
                ? "기존 프로필 캘린더의 ID와 권한을 목록에서 확인할 수 없습니다." : "실제 Google 캘린더 목록을 읽어 확인했습니다.");
        }
        private async Task ReadProfilePreview()
        {
            if (LastPreview != null && LastPreview.Window.IsVisible) { LastPreview.Window.Activate(); return; }
            StoreProfile(); ApplyDraft(); ConnectionProfiles.Validate(draft, draft.Connections);
            if (activeProfile == null || activeProfile.Mode != "excel") throw new InvalidOperationException("Excel 달력 프로필을 선택하세요.");
            var link = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<CalendarConnectionProfile>(JsonData.Serialize(activeProfile));
            var account = draft.Accounts.Single(a => a.Id == link.AccountId);
            var options = link.ExcelOptions == null ? new ExcelReadOptions() :
                new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ExcelReadOptions>(JsonData.Serialize(link.ExcelOptions));
            options.DocumentId = link.DocumentId; options.Worksheet = link.Worksheet;
            var captured = Copy(draft); captured.SourceDocumentId = link.DocumentId;
            captured.SourceWorkbookPath = link.WorkbookPath; captured.SourceWorksheet = link.Worksheet;
            Func<CancellationToken, Task<WorkbookEvents>> reread = token => owner.IsVerification && SyntheticReader != null ?
                SyntheticReader(captured, token) : Task.Run(() => ExcelEventParser.Read(link.WorkbookPath, options), token);
            var workbook = await reread(cancellation.Token); if (closed) return;
            var repo = new SyncRepository(Path.Combine(owner.Root, owner.IsVerification ? "evidence/" + owner.VerificationStateFile : ".state/sync-state.json"));
            repo.ResolveIdentities(workbook);
            ICalendarGateway gateway; List<RemoteEvent> remote;
            if (owner.IsVerification && SyntheticGateway != null) { gateway = SyntheticGateway; remote = await gateway.List(link.CalendarId, owner.Now, cancellation.Token); }
            else {
                var google = workflow.GoogleExisting(draft, account); gateway = google;
                remote = await workflow.Inspect(google, repo, account.GoogleSubject, link.CalendarId, link.DocumentId, owner.Now, cancellation.Token);
            }
            if (closed) return;
            var engine = new SyncEngine(repo); var plan = engine.Plan(workbook, account.GoogleSubject, link.CalendarId, remote, draft.GoogleColorId, owner.Now);
            plan.AccountEmail = account.VerifiedEmail;
            LastPreview = new PreviewDialog(Window, owner.Root, repo, engine, plan, gateway, reread, owner.IsVerification, owner.Now, true);
            LastPreview.Window.Show(); Status("프로필의 지원 xlsx 양식을 읽었습니다. 미리보기에서 확인한 뒤에만 동기화합니다.");
        }
    }
}
