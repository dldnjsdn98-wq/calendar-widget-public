using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    public sealed class AccountConfiguration
    {
        public string Id { get; set; }
        public string ProviderId { get; set; }
        public string Email { get; set; }
        public string DisplayName { get; set; }
        public List<string> CalendarIds { get; set; }
        public List<string> NativeCalendarIds { get; set; }
        public string TargetCalendarId { get; set; }
        public string VerifiedEmail { get; set; } public string GoogleSubject { get; set; }
        public override string ToString() { return String.IsNullOrWhiteSpace(DisplayName) ? "Google 계정" : DisplayName; }
    }
    public sealed class WidgetConfiguration
    {
        public string SelectedProfileId { get; set; }
        public List<string> VisibleCalendarKeys { get; set; }
        public bool Pinned { get; set; }
        public double Left { get; set; } public double Top { get; set; }
        public double Width { get; set; } public double Height { get; set; }
        public List<AccountConfiguration> Accounts { get; set; }
        public List<CalendarConnectionProfile> Connections { get; set; }
        public Dictionary<string, List<string>> VisibleFields { get; set; }
        public string SourceWorkbookPath { get; set; }
        public string SourceWorksheet { get; set; }
        public string SourceKind { get; set; } public string SourceDocumentId { get; set; }
        public ExcelReadOptions ExcelOptions { get; set; }
        public string OAuthClientFile { get; set; } public string GoogleColorId { get; set; }
        public string MicrosoftClientId { get; set; } public string MicrosoftTenant { get; set; }
        public string SourceDriveId { get; set; } public string SourceItemId { get; set; }
        public bool UseWorkbook { get; set; }
        public bool UseConnectedCalendar { get; set; }
        public string DisplayAccountId { get; set; }
        public string ThemeMode { get; set; }
        public string FontFamilyName { get; set; }
        public double DisplayFontSize { get; set; }
        public bool ChangeAlertsEnabled { get; set; }
        public string ChangeAlertsPausedUntilUtc { get; set; }
        public string ReleaseFeed { get; set; }
        public string SearchText { get; set; }
        public string QaFilter { get; set; }
        public string ViewDensity { get; set; }
        public List<WidgetViewPreset> ViewPresets { get; set; }
        public WidgetConfiguration()
        {
            SelectedProfileId = "brownfarm"; Left = -1; Top = -1; Width = 360; Height = 470;
            ThemeMode = "light"; FontFamilyName = "Malgun Gothic"; DisplayFontSize = 13;
            ChangeAlertsEnabled = true;
            ReleaseFeed = UpdateCore.DefaultFeed;
            SearchText = ""; QaFilter = "all"; ViewDensity = "comfortable"; ViewPresets = new List<WidgetViewPreset>();
            Accounts = new List<AccountConfiguration>(); VisibleFields = new Dictionary<string, List<string>>();
            SourceWorksheet = "이벤트확인";
            SourceKind = "local"; SourceDocumentId = Guid.NewGuid().ToString("N"); ExcelOptions = new ExcelReadOptions(); MicrosoftTenant = "organizations"; GoogleColorId = "";
        }
    }
    public static class ConfigurationStore
    {
        public static WidgetConfiguration Load(string path)
        {
            if (!File.Exists(path)) return new WidgetConfiguration();
            var data = new JavaScriptSerializer().Deserialize<WidgetConfiguration>(File.ReadAllText(path));
            if (data == null) throw new InvalidDataException("설정 파일이 비어 있습니다.");
            if (data.Accounts == null) data.Accounts = new List<AccountConfiguration>();
            if (data.VisibleFields == null) data.VisibleFields = new Dictionary<string, List<string>>();
            if (data.ExcelOptions == null) data.ExcelOptions = new ExcelReadOptions();
            if (data.ReleaseFeed == null) data.ReleaseFeed = UpdateCore.DefaultFeed;
            if (data.ViewPresets == null) data.ViewPresets = new List<WidgetViewPreset>();
            if (data.SearchText == null) data.SearchText = "";
            if (data.QaFilter == null) data.QaFilter = "all";
            if (data.ViewDensity != "compact" && data.ViewDensity != "comfortable") data.ViewDensity = "comfortable";
            ThemeManager.Normalize(data);
            if (String.IsNullOrWhiteSpace(data.SourceDocumentId)) data.SourceDocumentId = Guid.NewGuid().ToString("N");
            return data;
        }
        public static void Save(string path, WidgetConfiguration data)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
    public sealed class WidgetViewPreset
    {
        public string Name { get; set; }
        public string SelectedProfileId { get; set; }
        public List<string> VisibleCalendarKeys { get; set; }
        public string SearchText { get; set; }
        public string QaFilter { get; set; }
        public string ViewDensity { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
    public interface ICalendarProvider
    {
        string Id { get; }
        string ConnectionState(AccountConfiguration account);
        ICalendarSource CreateSource(AccountConfiguration account);
    }
    public sealed class SampleProvider : ICalendarProvider
    {
        private readonly string path;
        public SampleProvider(string path) { this.path = path; }
        public string Id { get { return "local-sample"; } }
        public string ConnectionState(AccountConfiguration account) { return "로컬 샘플"; }
        public ICalendarSource CreateSource(AccountConfiguration account) { return new LocalSampleSource(path); }
    }
    public sealed class GoogleCalendarProvider : ICalendarProvider
    {
        public string Id { get { return "google"; } }
        public string ConnectionState(AccountConfiguration account) { return "미연결 · OAuth 구성 및 사용자 승인이 필요합니다."; }
        public ICalendarSource CreateSource(AccountConfiguration account)
        {
            throw new InvalidOperationException("이메일 입력은 인증이 아닙니다. 로컬 앱용 OAuth 연결을 먼저 구성해야 합니다.");
        }
    }
}
