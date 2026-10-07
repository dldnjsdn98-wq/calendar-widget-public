using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CalendarWidget
{
    public sealed class CalendarConnectionProfile
    {
        public string Id { get; set; }
        public string AccountId { get; set; }
        public string CalendarId { get; set; }
        public string Mode { get; set; }
        public string DisplayName { get; set; }
        public bool DisplayOnly { get; set; }
        public string WorkbookPath { get; set; }
        public string Worksheet { get; set; }
        public string DocumentId { get; set; }
        public ExcelReadOptions ExcelOptions { get; set; }
        public override string ToString() { return String.IsNullOrWhiteSpace(DisplayName) ? (Mode == "excel" ? "Excel 달력" : "Google 직접") : DisplayName; }
    }
    public static class ConnectionProfiles
    {
        public static List<CalendarConnectionProfile> Effective(WidgetConfiguration config)
        {
            if (config.Connections != null && config.Connections.Count > 0) {
                var explicitProfiles = new List<CalendarConnectionProfile>(config.Connections);
                foreach (var account in config.Accounts.Where(a => !String.IsNullOrEmpty(a.GoogleSubject) && !String.IsNullOrWhiteSpace(a.TargetCalendarId))) {
                    if (explicitProfiles.Any(p => p.AccountId == account.Id && p.CalendarId == account.TargetCalendarId)) continue;
                    var restoredId = "legacy-" + SyncHash.Text(account.Id + "|" + account.TargetCalendarId);
                    if (explicitProfiles.Any(p => p.Id == restoredId)) restoredId = "legacy-restored-" + SyncHash.Text(account.Id + "|" + account.TargetCalendarId);
                    explicitProfiles.Add(new CalendarConnectionProfile { Id = restoredId,
                        AccountId = account.Id, CalendarId = account.TargetCalendarId, Mode = "excel", WorkbookPath = config.SourceWorkbookPath,
                        Worksheet = config.SourceWorksheet, DocumentId = config.SourceDocumentId, ExcelOptions = config.ExcelOptions });
                }
                return explicitProfiles;
            }
            var profiles = new List<CalendarConnectionProfile>();
            foreach (var account in config.Accounts.Where(a => !String.IsNullOrEmpty(a.GoogleSubject))) {
                if (!String.IsNullOrWhiteSpace(account.TargetCalendarId)) profiles.Add(new CalendarConnectionProfile {
                    Id = "legacy-" + SyncHash.Text(account.Id + "|" + account.TargetCalendarId), AccountId = account.Id,
                    CalendarId = account.TargetCalendarId, Mode = "excel", WorkbookPath = config.SourceWorkbookPath,
                    Worksheet = config.SourceWorksheet, DocumentId = config.SourceDocumentId, ExcelOptions = config.ExcelOptions });
                foreach (var id in CalendarAccess.SelectedIds(account).Where(id => id != account.TargetCalendarId))
                    profiles.Add(new CalendarConnectionProfile { Id = "legacy-native-" + SyncHash.Text(account.Id + "|" + id), AccountId = account.Id,
                        CalendarId = id, Mode = "google-direct" });
            }
            return profiles;
        }
        public static void Validate(WidgetConfiguration config, IList<CalendarConnectionProfile> profiles)
        {
            if (profiles == null || profiles.Any(p => p == null || String.IsNullOrWhiteSpace(p.Id) || String.IsNullOrWhiteSpace(p.AccountId) ||
                String.IsNullOrWhiteSpace(p.CalendarId) || (p.Mode != "excel" && p.Mode != "google-direct"))) throw new InvalidDataException("캘린더 연결 설정이 올바르지 않습니다.");
            if (profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != profiles.Count ||
                profiles.GroupBy(p => p.AccountId + "|" + p.CalendarId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidDataException("같은 계정과 캘린더가 중복 연결됐습니다.");
            foreach (var profile in profiles) {
                if (config.Accounts.Count(a => a.Id == profile.AccountId && !String.IsNullOrEmpty(a.GoogleSubject)) != 1)
                    throw new InvalidDataException("연결된 Google 계정을 확인할 수 없습니다.");
                if (profile.Mode == "excel" && (String.IsNullOrWhiteSpace(profile.WorkbookPath) ||
                    !String.Equals(Path.GetExtension(profile.WorkbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase) ||
                    String.IsNullOrWhiteSpace(profile.DocumentId) || String.IsNullOrWhiteSpace(profile.Worksheet)))
                    throw new InvalidDataException("Excel 달력에는 지원하는 xlsx 파일, 시트, 원본 ID가 필요합니다.");
            }
        }
        public static string Name(string actual, string display)
        {
            return String.IsNullOrWhiteSpace(display) || String.Equals(actual, display, StringComparison.Ordinal) ? actual : display.Trim() + " · " + actual;
        }
    }
}
