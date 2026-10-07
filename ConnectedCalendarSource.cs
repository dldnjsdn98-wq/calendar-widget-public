using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public sealed class SnapshotSource : ICalendarSource
    {
        private readonly CalendarSnapshot snapshot;
        public SnapshotSource(CalendarSnapshot snapshot) { CalendarRules.Validate(snapshot); this.snapshot = snapshot; }
        public CalendarSnapshot Load() { return snapshot; }
    }
    public static class ConnectedCalendarSource
    {
        public static AccountConfiguration Account(WidgetConfiguration config)
        {
            var accounts = LinkedAccounts(config);
            var account = accounts.FirstOrDefault(a => a.Id == config.DisplayAccountId);
            if (account == null && accounts.Count == 1) account = accounts[0];
            if (account == null) throw new InvalidOperationException("표시할 연결 계정을 선택하세요."); return account;
        }
        public static List<AccountConfiguration> LinkedAccounts(WidgetConfiguration config) { var profiles = ConnectionProfiles.Effective(config); return config.Accounts.Where(a => !String.IsNullOrEmpty(a.GoogleSubject) && profiles.Any(p => p.AccountId == a.Id)).ToList(); }
        public static string CalendarKey(string account, string calendar) { return "linked-" + SyncHash.Text(account + "|" + calendar); }
        public static string Scope(WidgetConfiguration config) { var account = Account(config); var profiles = ConnectionProfiles.Effective(config); return profiles.Count == 1 && profiles[0].Mode == "excel" && profiles[0].CalendarId == account.TargetCalendarId && profiles[0].DocumentId == config.SourceDocumentId ? SyncEngine.Scope(account.GoogleSubject, account.TargetCalendarId, config.SourceDocumentId) : "display-" + SyncHash.Text(account.Id + "|" + String.Join("|", profiles.OrderBy(p => p.Id).Select(p => p.Id + ":" + p.AccountId + ":" + p.CalendarId + ":" + p.Mode + ":" + p.DocumentId))); }
        public static CalendarSnapshot Empty(WidgetConfiguration config)
        {
            return new CalendarSnapshot { SchemaVersion = 1, Mode = "google-loading", Scope = Scope(config), TimeZone = "Asia/Seoul", ObservedAt = DateTimeOffset.UtcNow.ToString("o"),
                Events = new List<CalendarEvent>(), Calendars = PlaceholderCalendars(config) };
        }
        private static List<CalendarProfile> PlaceholderCalendars(WidgetConfiguration config)
        {
            return ConnectionProfiles.Effective(config).Select(link => {
                var account = config.Accounts.Single(a => a.Id == link.AccountId);
                var profile = Profile(String.IsNullOrWhiteSpace(link.DisplayName) ? "Google 캘린더" : link.DisplayName, "#9569D5");
                if (link.Mode == "google-direct") profile.Fields = new List<FieldDefinition>();
                profile.Id = CalendarKey(account.GoogleSubject, link.CalendarId);
                profile.AccountId = account.GoogleSubject; profile.AccountName = account.VerifiedEmail; profile.RemoteCalendarId = link.CalendarId;
                return profile;
            }).ToList();
        }
        private static CalendarProfile Profile(string name, string color)
        {
            return new CalendarProfile { Id = "brownfarm", Name = name, Color = color, Fields = StatusFields() };
        }
        public static List<FieldDefinition> StatusFields()
        {
            return new List<FieldDefinition> {
                new FieldDefinition { Key = "setup", Label = "세팅" }, new FieldDefinition { Key = "check", Label = "확인" },
                new FieldDefinition { Key = "alphaTester", Label = "알파와 테스터" }, new FieldDefinition { Key = "realTester", Label = "리얼과 테스터" } };
        }
        public static CalendarSnapshot Project(WorkbookEvents workbook, SyncRepository repo, string scope, string name, string color, List<RemoteEvent> remote, DateTimeOffset now)
        { return Project(workbook, repo, scope, name, color, remote, now, null, null, null); }
        public static CalendarSnapshot Project(WorkbookEvents workbook, SyncRepository repo, string scope, string name, string color, List<RemoteEvent> remote, DateTimeOffset now, string account, string calendar, string email, bool includeUnlinked = true)
        {
            var snapshot = new CalendarSnapshot { SchemaVersion = 1, Mode = "google", Scope = scope, TimeZone = "Asia/Seoul", ObservedAt = now.ToString("o"),
                Warnings = workbook.Warnings, Calendars = new List<CalendarProfile> { Profile(name, color) }, Events = new List<CalendarEvent>() };
            var profile = snapshot.Calendars[0]; if (account != null && calendar != null) { profile.Id = CalendarKey(account, calendar); profile.AccountId = account; profile.RemoteCalendarId = calendar; profile.AccountName = email; }
            profile.ReadSucceeded = true;
            foreach (var item in remote) {
                var candidates = repo.State.Mappings.Where(m => m.GoogleEventId == item.Id &&
                    (m.Scope == scope || (account != null && calendar != null && m.AccountId == account && m.CalendarId == calendar))).ToList();
                var mapping = candidates.Count == 1 ? candidates[0] : null;
                var source = mapping == null ? null : workbook.Events.SingleOrDefault(e => !e.UnlinkedSource && (
                    !String.IsNullOrEmpty(e.ExplicitId) ? "e" + SyncHash.Text(e.ExplicitId + "|" + e.Phase) == mapping.StableId :
                    repo.State.Identities.Any(i => i.DocumentId == mapping.DocumentId && i.StableId == mapping.StableId && i.Aliases != null && i.Aliases.Contains(e.Alias))));
                bool matches = source != null && mapping.LastHash == item.Hash;
                string remoteIssue, remoteBinding;
                var remoteStatus = RemoteStatusProjection.Read(item, out remoteIssue, out remoteBinding);
                var fields = matches ? new Dictionary<string, string>(source.Fields) : new[] { "setup", "check", "alphaTester", "realTester" }.ToDictionary(k => k, k => source == null ? "미연결 · 원본 행 없음" : "미연결 · 원격 일정 변경");
                string statusConflict = remoteIssue;
                bool differing = false;
                if (remoteStatus != null) {
                    fields = GoogleOnlyStatus.DisplayValues(remoteStatus);
                    if (matches && source.StatusBinding != null) {
                        differing = !String.IsNullOrEmpty(remoteBinding) && remoteBinding != source.StatusBinding.Signature ||
                            source.StatusBinding.Cells.Any(c => remoteStatus.ContainsKey(c.Key) && remoteStatus[c.Key] != (c.Value.OriginalValue ?? ""));
                        if (differing) statusConflict = "Google 상태와 이 PC의 Excel 값이 다릅니다. 자동으로 덮어쓰지 않았습니다.";
                    }
                }
                snapshot.Events.Add(new CalendarEvent { Id = account == null ? item.Id : profile.Id + ":" + item.Id, CalendarId = profile.Id, Title = item.Summary, AllDay = item.AllDay,
                    StatusContext = matches && source.StatusBinding != null && account != null ? new StatusEditContext { Scope = scope, AccountId = account, CalendarId = calendar, GoogleEventId = item.Id, GoogleVersion = item.ETag, Binding = source.StatusBinding, ReadOnly = differing || !String.IsNullOrEmpty(remoteIssue) } : null,
                    Origin = source == null ? "google-direct" : "excel",
                    Start = item.AllDay ? item.Start.ToString("yyyy-MM-dd") : item.Start.ToString("o"), End = item.AllDay ? item.End.ToString("yyyy-MM-dd") : item.End.ToString("o"), Fields = fields,
                    RemoteStatus = remoteStatus, RemoteStatusIssue = remoteIssue, StatusConflict = statusConflict });
            }
            foreach (var source in workbook.Events.Where(e => includeUnlinked && e.UnlinkedSource && (e.End ?? e.Start.AddTicks(1)) > new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)))) {
                if (remote.Any(r => r.Summary == source.Title && r.Start == source.Start)) continue;
                var fields = new[] { "setup", "check", "alphaTester", "realTester" }.ToDictionary(k => k, k => "원본 셀 연결 없음 · 편집 보류");
                snapshot.Events.Add(new CalendarEvent { Id = profile.Id + ":unlinked:" + source.Alias, CalendarId = profile.Id,
                    Title = "[원본 확인 필요] " + source.Title, Start = source.Start.ToString("o"), End = source.End.HasValue ? source.End.Value.ToString("o") : null,
                    Fields = fields, Origin = "excel-unlinked", StatusContext = null });
            }
            CalendarRules.Validate(snapshot); return snapshot;
        }
        public static CalendarSnapshot ProjectNative(RemoteCalendar calendar, List<RemoteEvent> remote, DateTimeOffset now, string account, string email)
        {
            var profile = Profile(calendar.Name, calendar.Color ?? "#9569D5");
            profile.Id = CalendarKey(account, calendar.Id); profile.AccountId = account; profile.AccountName = email; profile.RemoteCalendarId = calendar.Id;
            profile.ReadSucceeded = true;
            profile.Fields = new List<FieldDefinition>();
            var snapshot = new CalendarSnapshot { SchemaVersion = 1, Mode = "google", TimeZone = "Asia/Seoul", ObservedAt = now.ToString("o"),
                Calendars = new List<CalendarProfile> { profile }, Events = new List<CalendarEvent>() };
            foreach (var item in remote) {
                bool writable = CalendarAccess.CanWrite(calendar);
                bool hasStatus = item.PrivateProperties != null && item.PrivateProperties.ContainsKey("cw.status.version") ||
                    (item.Description ?? "").Contains(StatusPayloadPolicy.BeginMarker);
                var context = writable || hasStatus ? GoogleOnlyStatus.Context(account, calendar.Id, item, writable) : null;
                string remoteIssue, remoteBinding;
                var remoteStatus = RemoteStatusProjection.Read(item, out remoteIssue, out remoteBinding);
                snapshot.Events.Add(new CalendarEvent { Id = profile.Id + ":" + item.Id, CalendarId = profile.Id,
                    Title = item.Summary, AllDay = item.AllDay, Start = item.AllDay ? item.Start.ToString("yyyy-MM-dd") : item.Start.ToString("o"),
                    End = item.AllDay ? item.End.ToString("yyyy-MM-dd") : item.End.ToString("o"),
                    Fields = remoteStatus != null ? GoogleOnlyStatus.DisplayValues(remoteStatus) : new Dictionary<string, string>(),
                    StatusContext = context, Origin = "google-direct", RemoteStatus = remoteStatus, RemoteStatusIssue = remoteIssue, StatusConflict = remoteIssue });
            }
            CalendarRules.Validate(snapshot); return snapshot;
        }
        private static async Task<WorkbookEvents> ReadProfileWorkbook(SourceWorkflow workflow, WidgetConfiguration config, CalendarConnectionProfile link, CancellationToken cancellation)
        {
            if (link.Id.StartsWith("legacy-", StringComparison.Ordinal) && config.SourceKind == "sharepoint")
                return await workflow.Read(config, null, cancellation);
            var options = link.ExcelOptions == null ? new ExcelReadOptions() :
                new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ExcelReadOptions>(JsonData.Serialize(link.ExcelOptions));
            options.DocumentId = link.DocumentId; options.Worksheet = link.Worksheet;
            options.AllowUnlinkedSortedRows = true;
            return await Task.Run(() => ExcelEventParser.Read(link.WorkbookPath, options), cancellation);
        }
        public static async Task<CalendarSnapshot> Read(string root, WidgetConfiguration config, DateTimeOffset now, CancellationToken cancellation)
        {
            var links = ConnectionProfiles.Effective(config); ConnectionProfiles.Validate(config, links); Account(config);
            using (var transport = new HttpTransport()) {
                var workflow = new SourceWorkflow(root, transport);
                var repo = new SyncRepository(Path.Combine(root, ".state/sync-state.json"));
                var result = new CalendarSnapshot { SchemaVersion = 1, Mode = "google", Scope = Scope(config), TimeZone = "Asia/Seoul", ObservedAt = now.ToString("o"),
                    Warnings = new List<string>(), Calendars = new List<CalendarProfile>(), Events = new List<CalendarEvent>() };
                foreach (var group in links.GroupBy(p => p.AccountId)) {
                    cancellation.ThrowIfCancellationRequested();
                    var account = config.Accounts.Single(a => a.Id == group.Key);
                    var google = workflow.GoogleExisting(config, account);
                    var available = await google.Calendars(cancellation);
                    var configuredIds = new HashSet<string>(group.Select(p => p.CalendarId), StringComparer.Ordinal);
                    foreach (var link in group) {
                        var target = available.SingleOrDefault(c => c.Id == link.CalendarId);
                        if (target == null) { result.Warnings.Add("연결된 캘린더가 현재 계정 목록에 없습니다."); continue; }
                        if (!CalendarAccess.CanRead(target)) { result.Calendars.Add(FreeBusyProfile(target, account)); continue; }
                        if (link.Mode == "excel" && !CalendarAccess.CanWrite(target)) {
                            var readerEvents = await google.List(target.Id, new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)), cancellation);
                            var readerProfile = ProjectNative(target, readerEvents, now, account.GoogleSubject, account.VerifiedEmail);
                            readerProfile.Calendars[0].AccessRole = target.AccessRole;
                            result.Calendars.AddRange(readerProfile.Calendars); result.Events.AddRange(readerProfile.Events);
                            result.Warnings.Add("읽기 전용 캘린더에는 Excel 동기화와 상태 편집을 사용할 수 없습니다."); continue;
                        }
                        if (target == null || (link.Mode == "excel" ? !CalendarAccess.CanWrite(target) : !CalendarAccess.CanRead(target)))
                            throw new InvalidOperationException("연결된 캘린더의 ID 또는 읽기/쓰기 권한을 확인할 수 없습니다.");
                        var remote = await google.List(link.CalendarId, new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)), cancellation);
                        var label = ConnectionProfiles.Name(target.Name, link.DisplayName);
                        CalendarSnapshot one;
                        bool mappedWorkbook = link.Mode == "google-direct" && !String.IsNullOrWhiteSpace(link.WorkbookPath) &&
                            !String.IsNullOrWhiteSpace(link.DocumentId) && !String.IsNullOrWhiteSpace(link.Worksheet) &&
                            File.Exists(link.WorkbookPath) && repo.State.Mappings.Any(m => m.AccountId == account.GoogleSubject &&
                                m.CalendarId == link.CalendarId && m.DocumentId == link.DocumentId);
                        if (link.Mode == "google-direct" && !mappedWorkbook) {
                            one = ProjectNative(new RemoteCalendar { Id = target.Id, Name = label, Color = target.Color, AccessRole = target.AccessRole }, remote, now, account.GoogleSubject, account.VerifiedEmail);
                        } else {
                            try {
                                var workbook = await ReadProfileWorkbook(workflow, config, link, cancellation);
                                one = Project(workbook, repo, SyncEngine.Scope(account.GoogleSubject, link.CalendarId, link.DocumentId), label,
                                    target.Color ?? "#9569D5", remote, now, account.GoogleSubject, link.CalendarId, account.VerifiedEmail, link.CalendarId == account.TargetCalendarId);
                                if (one.Warnings != null) result.Warnings.AddRange(one.Warnings);
                            } catch (InvalidDataException) {
                                one = ProjectNative(new RemoteCalendar { Id = target.Id, Name = label, Color = target.Color, AccessRole = target.AccessRole },
                                    remote, now, account.GoogleSubject, account.VerifiedEmail);
                                result.Warnings.Add("Excel 원본을 읽지 못해 이 캘린더는 Google 일정만 표시합니다. 원본 XLSX를 확인하세요. 상태 편집은 보류됩니다.");
                            }
                        }
                        one.Calendars[0].AccessRole = target.AccessRole;
                        result.Calendars.AddRange(one.Calendars); result.Events.AddRange(one.Events);
                    }
                    foreach (var target in available.Where(c => !String.IsNullOrWhiteSpace(c.Id) && !configuredIds.Contains(c.Id))) {
                        cancellation.ThrowIfCancellationRequested();
                        if (!CalendarAccess.CanRead(target)) { result.Calendars.Add(FreeBusyProfile(target, account)); continue; }
                        try {
                            var remote = await google.List(target.Id, new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)), cancellation);
                            var one = ProjectNative(target, remote, now, account.GoogleSubject, account.VerifiedEmail);
                            one.Calendars[0].AccessRole = target.AccessRole;
                            result.Calendars.AddRange(one.Calendars); result.Events.AddRange(one.Events);
                        } catch (CalendarApiException) {
                            result.Calendars.Add(FreeBusyProfile(target, account));
                            result.Warnings.Add("일부 캘린더의 일정 상세정보를 읽지 못했습니다. 접근 권한을 확인하세요.");
                        }
                    }
                }
                CalendarRules.Validate(result); return result;
            }
        }
        private static CalendarProfile FreeBusyProfile(RemoteCalendar target, AccountConfiguration account)
        {
            return new CalendarProfile { Id = CalendarKey(account.GoogleSubject, target.Id), RemoteCalendarId = target.Id,
                AccountId = account.GoogleSubject, AccountName = account.VerifiedEmail, Name = target.Name + " (상세정보 접근 제한)",
                Color = target.Color ?? "#687182", AccessRole = target.AccessRole, Fields = new List<FieldDefinition>() };
        }
    }
    public static class CalendarSelection
    {
        public static CalendarSnapshot Visible(CalendarSnapshot data, IList<string> selected)
        {
            if (selected == null) return data;
            var keys = new HashSet<string>(selected, StringComparer.Ordinal);
            var calendars = data.Calendars.Where(c => keys.Contains(c.Id)).ToList();
            return new CalendarSnapshot { SchemaVersion = data.SchemaVersion, Mode = data.Mode, TimeZone = data.TimeZone,
                ObservedAt = data.ObservedAt, Scope = data.Scope, Warnings = data.Warnings, Calendars = calendars,
                Events = data.Events.Where(e => keys.Contains(e.CalendarId)).ToList() };
        }
        public static List<CalendarProfile> Choices(CalendarSnapshot data)
        {
            var result = new List<CalendarProfile> { new CalendarProfile { Id = "", Name = "전체 캘린더" } };
            foreach (var calendar in data.Calendars) {
                bool duplicate = data.Calendars.Count(c => c.Name == calendar.Name) > 1;
                var label = calendar.Name + (duplicate ? " (" + (calendar.AccountName ?? calendar.AccountId ?? calendar.Id) + ")" : "");
                if (result.Any(p => p.Name == label)) label += " [" + calendar.Id.Substring(0, Math.Min(12, calendar.Id.Length)) + "]";
                result.Add(new CalendarProfile { Id = calendar.Id, Name = label });
            }
            return result;
        }
        public static string Resolve(CalendarSnapshot data, string selected)
        {
            if (data.Calendars.Any(c => c.Id == selected)) return selected;
            return selected == "brownfarm" && data.Mode == "google" && data.Calendars.Count == 1 ? data.Calendars[0].Id : "";
        }
    }
}
