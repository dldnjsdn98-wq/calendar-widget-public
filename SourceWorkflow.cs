using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public sealed class WorkbookDisplaySource : ICalendarSource
    {
        private readonly WorkbookEvents workbook; private readonly string mode; private readonly WidgetConfiguration configuration; private readonly string root;
        public WorkbookDisplaySource(WorkbookEvents workbook, string mode) : this(workbook, mode, null, null) { }
        public WorkbookDisplaySource(WorkbookEvents workbook, string mode, WidgetConfiguration configuration, string root) { this.workbook = workbook; this.mode = mode; this.configuration = configuration; this.root = root; }
        private static string StableId(SyncRepository repo, string document, SourceEvent source)
        {
            if (!String.IsNullOrEmpty(source.ExplicitId)) return "e" + SyncHash.Text(source.ExplicitId + "|" + source.Phase);
            var known = repo.State.Identities.Where(i => i.DocumentId == document && i.Aliases != null && i.Aliases.Contains(source.Alias)).ToList();
            return known.Count == 1 ? known[0].StableId : null;
        }
        private string StatusDocument(SyncRepository repo, AccountConfiguration account)
        {
            string previous = configuration.ExcelOptions == null ? null : configuration.ExcelOptions.DocumentId;
            if (String.IsNullOrEmpty(previous) || previous == workbook.DocumentId || workbook.Events.Count == 0) return workbook.DocumentId;
            // A downloaded copy may have a new local source ID. Reuse the earlier status identity only
            // when every source event uniquely matches its existing mapping for this exact account/calendar.
            string scope = SyncEngine.Scope(account.GoogleSubject, account.TargetCalendarId, previous);
            var mapped = repo.State.Mappings.Where(m => m.Scope == scope && m.DocumentId == previous && m.AccountId == account.GoogleSubject && m.CalendarId == account.TargetCalendarId && !String.IsNullOrEmpty(m.LastETag)).ToList();
            if (mapped.Count != workbook.Events.Count || repo.State.Mappings.Any(m => m.DocumentId == workbook.DocumentId && m.AccountId == account.GoogleSubject && m.CalendarId == account.TargetCalendarId)) return workbook.DocumentId;
            var stableIds = workbook.Events.Select(e => StableId(repo, previous, e)).ToList();
            if (stableIds.Any(String.IsNullOrEmpty) || stableIds.Distinct().Count() != stableIds.Count || stableIds.Any(id => mapped.Count(m => m.StableId == id) != 1)) return workbook.DocumentId;
            return previous;
        }
        public CalendarSnapshot Load()
        {
            var snapshot = new CalendarSnapshot { SchemaVersion = 1, Mode = mode, TimeZone = "Asia/Seoul", ObservedAt = workbook.ObservedAt.ToString("o"),
                Calendars = new List<CalendarProfile> { new CalendarProfile { Id = "brownfarm", Name = "원본 캘린더", Color = "#9569D5", Fields = new List<FieldDefinition> { new FieldDefinition { Key = "setup", Label = "세팅" }, new FieldDefinition { Key = "check", Label = "확인" }, new FieldDefinition { Key = "alphaTester", Label = "알파와 테스터" }, new FieldDefinition { Key = "realTester", Label = "리얼과 테스터" } } } }, Events = new List<CalendarEvent>() };
            AccountConfiguration account = null; SyncRepository repo = null;
            if (configuration != null && configuration.SourceKind == "local" && root != null) {
                var connected = configuration.Accounts.Where(a => !String.IsNullOrEmpty(a.GoogleSubject) && !String.IsNullOrEmpty(a.TargetCalendarId)).ToList();
                account = connected.SingleOrDefault(a => a.Id == configuration.DisplayAccountId);
                if (account == null && connected.Count == 1) account = connected[0];
                if (account != null) repo = new SyncRepository(Path.Combine(root, ".state/sync-state.json"));
            }
            string statusDocument = repo == null ? workbook.DocumentId : StatusDocument(repo, account);
            foreach (var source in workbook.Events) {
                StatusEditContext status = null;
                if (repo != null && source.StatusBinding != null && source.StatusBinding.Exact) {
                    string stable = StableId(repo, statusDocument, source);
                    string scope = SyncEngine.Scope(account.GoogleSubject, account.TargetCalendarId, statusDocument);
                    var matches = repo.State.Mappings.Where(m => m.Scope == scope && m.StableId == stable && m.AccountId == account.GoogleSubject && m.CalendarId == account.TargetCalendarId && m.DocumentId == statusDocument && !String.IsNullOrEmpty(m.LastETag)).ToList();
                    if (stable != null && matches.Count == 1) {
                        source.StatusBinding.DocumentId = statusDocument;
                        status = new StatusEditContext { Scope = scope, AccountId = account.GoogleSubject, CalendarId = account.TargetCalendarId, GoogleEventId = matches[0].GoogleEventId, GoogleVersion = matches[0].LastETag, Binding = source.StatusBinding };
                    }
                }
                snapshot.Events.Add(new CalendarEvent { Id = source.StableId ?? source.Alias, CalendarId = "brownfarm", Title = source.Title, StatusContext = status,
                Start = source.AllDay ? source.Start.ToString("yyyy-MM-dd") : source.Start.ToString("o"), End = source.End.HasValue ? (source.AllDay ? source.End.Value.ToString("yyyy-MM-dd") : source.End.Value.ToString("o")) : null,
                AllDay = source.AllDay, IsPoint = !source.End.HasValue, Fields = source.Fields });
            }
            CalendarRules.Validate(snapshot); return snapshot;
        }
    }
    public sealed class SourceWorkflow
    {
        private readonly string root; private readonly IHttpTransport transport;
        public readonly ProtectedStore Credentials, PrivateSources; public readonly OAuthService Auth;
        public SourceWorkflow(string root, IHttpTransport transport)
        {
            this.root = root; this.transport = transport;
            Credentials = new ProtectedStore(Path.Combine(root, ".credentials")); PrivateSources = new ProtectedStore(Path.Combine(root, ".sources")); Auth = new OAuthService(transport, Credentials);
        }
        public async Task<WorkbookEvents> Read(WidgetConfiguration config, string unsavedSharingLink, CancellationToken cancellation)
        {
            string path = config.SourceWorkbookPath;
            if (config.SourceKind == "sharepoint")
            {
                string link = unsavedSharingLink;
                if (String.IsNullOrWhiteSpace(link) && PrivateSources.Exists(config.SourceDocumentId)) link = PrivateSources.Load(config.SourceDocumentId);
                if (String.IsNullOrWhiteSpace(link) && (String.IsNullOrEmpty(config.SourceDriveId) || String.IsNullOrEmpty(config.SourceItemId))) throw new InvalidOperationException("SharePoint 링크를 입력하세요.");
                var reader = new SharePointReader(transport, Auth, OAuthClientSettings.Microsoft(config.MicrosoftClientId, config.MicrosoftTenant ?? "organizations"), RuntimePermissions.OAuthEnabled);
                byte[] bytes = await reader.Download(link, config.SourceDriveId, config.SourceItemId, cancellation);
                var directory = Path.Combine(root, ".cache"); Directory.CreateDirectory(directory); path = Path.Combine(directory, SyncHash.Text(config.SourceDocumentId) + ".xlsx"); File.WriteAllBytes(path, bytes);
            }
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("원본 xlsx를 선택하세요.");
            var options = config.ExcelOptions ?? new ExcelReadOptions(); options.DocumentId = config.SourceDocumentId; options.Worksheet = config.SourceWorksheet;
            return await Task.Run(() => ExcelEventParser.Read(path, options), cancellation);
        }
        public GoogleGateway Google(WidgetConfiguration config, AccountConfiguration account)
        {
            if (account == null || String.IsNullOrWhiteSpace(account.GoogleSubject)) throw new InvalidOperationException("실제 Google 계정 연결이 필요합니다.");
            return new GoogleGateway(transport, Auth, OAuthClientSettings.Google(config.OAuthClientFile), "google-" + account.Id, RuntimePermissions.OAuthEnabled);
        }
        public GoogleGateway GoogleExisting(WidgetConfiguration config, AccountConfiguration account)
        {
            if (account == null || String.IsNullOrWhiteSpace(account.Id) || String.IsNullOrWhiteSpace(account.GoogleSubject) ||
                !Credentials.Exists("google-" + account.Id)) throw new InvalidOperationException("저장된 Google 연결이 없습니다. 새 로그인은 사용자가 직접 시작해야 합니다.");
            var client = OAuthClientSettings.Google(config.OAuthClientFile);
            var token = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<OAuthToken>(Credentials.Load("google-" + account.Id));
            if (token == null || token.ClientId != client.ClientId ||
                (token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2) && String.IsNullOrEmpty(token.RefreshToken)))
                throw new InvalidOperationException("기존 Google 연결을 사용할 수 없습니다. 재연결이 필요합니다.");
            return new GoogleGateway(transport, Auth, client, "google-" + account.Id, true, true);
        }
        public async Task<List<RemoteEvent>> Inspect(GoogleGateway google, SyncRepository repo, string account, string calendar, string document, DateTimeOffset now, CancellationToken cancellation)
        {
            var calendars = await google.Calendars(cancellation); var target = calendars.FirstOrDefault(c => c.Id == calendar);
            if (!CalendarAccess.CanWrite(target)) throw new InvalidOperationException("현재 권한은 본인 소유 캘린더만 지원합니다. 대상의 소유권을 확인하세요.");
            var remote = await google.List(calendar, new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)), cancellation);
            string scope = SyncEngine.Scope(account, calendar, document);
            foreach (var id in repo.State.Mappings.Where(m => m.Scope == scope).Select(m => m.GoogleEventId).Concat(repo.State.Journal.Where(j => j.Scope == scope).Select(j => j.GoogleEventId)).Distinct())
            {
                if (remote.Any(r => r.Id == id)) continue; var item = await google.Get(calendar, id, cancellation); if (item != null) remote.Add(item);
            }
            return remote;
        }
    }
}
