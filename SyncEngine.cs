using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    public static class SyncHash
    {
        public static string Bytes(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static string Text(string value) { return Bytes(Encoding.UTF8.GetBytes(value ?? "")); }
        public static string Event(string title, DateTimeOffset start, DateTimeOffset end, bool allDay, string color) { return Text(title + "|" + start.ToUniversalTime().ToString("o") + "|" + end.ToUniversalTime().ToString("o") + "|" + allDay + "|" + (color ?? "")); }
    }
    public sealed class SourceIdentity
    {
        public string DocumentId { get; set; } public string StableId { get; set; } public string Title { get; set; }
        public bool Confirmed { get; set; } public List<string> Aliases { get; set; }
        public List<string> ConfirmedTargets { get; set; }
    }
    public sealed class EventMapping
    {
        public string Scope { get; set; } public string StableId { get; set; } public string GoogleEventId { get; set; }
        public string LastHash { get; set; } public string LastETag { get; set; }
        public string LastSourceTitle { get; set; }
        public string AccountId { get; set; } public string CalendarId { get; set; } public string DocumentId { get; set; }
    }
    public sealed class SyncJournal
    {
        public string Scope { get; set; } public string StableId { get; set; } public string GoogleEventId { get; set; }
        public string DesiredHash { get; set; } public string State { get; set; } public string Operation { get; set; }
        public string AccountId { get; set; } public string CalendarId { get; set; } public string DocumentId { get; set; }
    }
    public sealed class SyncState
    {
        public int SchemaVersion { get; set; } public List<SourceIdentity> Identities { get; set; }
        public List<EventMapping> Mappings { get; set; } public List<SyncJournal> Journal { get; set; }
        public SyncState() { SchemaVersion = 1; Identities = new List<SourceIdentity>(); Mappings = new List<EventMapping>(); Journal = new List<SyncJournal>(); }
    }
    public sealed class SyncRepository
    {
        private readonly string path; public readonly SyncState State;
        public Action BeforeSave;
        public SyncRepository(string path)
        {
            this.path = path;
            State = File.Exists(path) ? new JavaScriptSerializer().Deserialize<SyncState>(File.ReadAllText(path)) : new SyncState();
            if (State == null || State.SchemaVersion != 1 || State.Identities == null || State.Mappings == null || State.Journal == null) throw new InvalidDataException("ID 매핑 파일 형식 오류. 자동 생성으로 덮어쓰지 않습니다.");
        }
        public void Save()
        {
            if (BeforeSave != null) BeforeSave(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp"; File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(State));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public void ResolveIdentities(WorkbookEvents workbook)
        {
            var previous = State.Identities.Where(i => i.DocumentId == workbook.DocumentId).ToList();
            var explicitKeys = new HashSet<string>();
            foreach (var source in workbook.Events)
            {
                if (!String.IsNullOrWhiteSpace(source.ExplicitId))
                {
                    string key = source.ExplicitId + "|" + source.Phase;
                    if (!explicitKeys.Add(key)) throw new InvalidDataException("원본 이벤트ID와 단계가 중복됐습니다.");
                    source.StableId = "e" + SyncHash.Text(key);
                }
                else
                {
                    var known = previous.Where(i => i.Aliases.Contains(source.Alias)).ToList();
                    if (known.Count > 1) throw new InvalidDataException("원본 별칭 ID가 중복됐습니다.");
                    source.StableId = known.Count == 1 ? known[0].StableId : null;
                }
            }
            var missing = previous.Where(i => !workbook.Events.Any(e => e.StableId == i.StableId)).ToList();
            foreach (var source in workbook.Events)
            {
                if (source.StableId == null) source.StableId = "g" + Guid.NewGuid().ToString("N");
                var identity = State.Identities.FirstOrDefault(i => i.DocumentId == workbook.DocumentId && i.StableId == source.StableId);
                if (identity == null)
                {
                    identity = new SourceIdentity { DocumentId = workbook.DocumentId, StableId = source.StableId, Title = source.Title, Aliases = new List<string>(), Confirmed = !String.IsNullOrEmpty(source.ExplicitId) };
                    State.Identities.Add(identity);
                }
                if (!identity.Aliases.Contains(source.Alias)) identity.Aliases.Add(source.Alias);
                source.IdentityNeedsReview = !identity.Confirmed && (missing.Count > 0 || String.IsNullOrEmpty(source.ExplicitId));
            }
            Save();
        }
        public void BindIdentity(WorkbookEvents workbook, SourceEvent source, string existingId)
        {
            if (workbook.Events.Any(e => !Object.ReferenceEquals(e, source) && e.StableId == existingId)) throw new InvalidDataException("다른 원본 행이 이미 사용하는 안정 ID입니다.");
            var identity = State.Identities.FirstOrDefault(i => i.DocumentId == workbook.DocumentId && i.StableId == existingId);
            if (identity == null) throw new InvalidDataException("같은 원본의 기존 ID를 선택하세요.");
            foreach (var other in State.Identities.Where(i => i.DocumentId == workbook.DocumentId && i.StableId != existingId)) other.Aliases.Remove(source.Alias);
            if (!identity.Aliases.Contains(source.Alias)) identity.Aliases.Add(source.Alias); identity.Confirmed = true;
            source.StableId = existingId; source.IdentityNeedsReview = false; Save();
        }
        public void ConfirmNew(WorkbookEvents workbook, SourceEvent source)
        {
            var identity = State.Identities.First(i => i.DocumentId == workbook.DocumentId && i.StableId == source.StableId);
            identity.Confirmed = true; source.IdentityNeedsReview = false; Save();
        }
        public bool NewConfirmedForTarget(WorkbookEvents workbook, SourceEvent source, string scope)
        {
            var identity = State.Identities.FirstOrDefault(i => i.DocumentId == workbook.DocumentId && i.StableId == source.StableId);
            return identity != null && identity.ConfirmedTargets != null && identity.ConfirmedTargets.Contains(scope);
        }
        public void ConfirmNewForTarget(WorkbookEvents workbook, SourceEvent source, string scope)
        {
            if (String.IsNullOrWhiteSpace(scope)) throw new InvalidDataException("동기화 대상을 다시 선택하세요.");
            var identity = State.Identities.First(i => i.DocumentId == workbook.DocumentId && i.StableId == source.StableId);
            if (identity.ConfirmedTargets == null) identity.ConfirmedTargets = new List<string>();
            if (identity.ConfirmedTargets.Contains(scope)) return;
            identity.ConfirmedTargets.Add(scope); Save();
        }
        public bool BindingConflict(string account, string calendar, string document, string scope, string stableId, string googleId)
        {
            return State.Mappings.Any(m => Conflicts(account, calendar, document, scope, stableId, googleId, m.AccountId, m.CalendarId, m.DocumentId, m.Scope, m.StableId, m.GoogleEventId)) ||
                State.Journal.Any(j => Conflicts(account, calendar, document, scope, stableId, googleId, j.AccountId, j.CalendarId, j.DocumentId, j.Scope, j.StableId, j.GoogleEventId));
        }
        private static bool Conflicts(string account, string calendar, string document, string scope, string stableId, string googleId, string oldAccount, string oldCalendar, string oldDocument, string oldScope, string oldStableId, string oldGoogleId)
        {
            if (scope == oldScope && stableId == oldStableId && googleId != oldGoogleId) return true;
            if (googleId != oldGoogleId) return false;
            bool sameTarget = scope == oldScope || (!String.IsNullOrEmpty(account) && !String.IsNullOrEmpty(calendar) && account == oldAccount && calendar == oldCalendar);
            // Legacy records with an unknown target are held across documents until their ownership can be verified.
            if (scope != oldScope && (String.IsNullOrEmpty(oldAccount) || String.IsNullOrEmpty(oldCalendar))) return true;
            return sameTarget && (stableId != oldStableId || (!String.IsNullOrEmpty(oldDocument) && oldDocument != document));
        }
        public void Import(string file, string scope, WorkbookEvents workbook, string account = null, string calendar = null)
        {
            if (new FileInfo(file).Length > 1000000) throw new InvalidDataException("매핑 파일이 너무 큽니다.");
            var seed = new JavaScriptSerializer().Deserialize<MappingImport>(File.ReadAllText(file));
            if (seed == null || seed.SchemaVersion != 1 || seed.Events == null) throw new InvalidDataException("매핑 가져오기 형식을 확인하세요.");
            var pending = new List<EventMapping>(); var unique = new HashSet<string>(); var remote = new HashSet<string>();
            foreach (var item in seed.Events)
            {
                var source = workbook.Events.FirstOrDefault(e => e.StableId == item.StableSourceId || (!String.IsNullOrEmpty(item.SourceAlias) && e.Alias == item.SourceAlias));
                if (source == null && item.SourceTitle != null && item.Start != null)
                {
                    var candidates = workbook.Events.Where(e => System.Text.RegularExpressions.Regex.Replace(e.BaseTitle ?? e.Title, @"\s+", "") == System.Text.RegularExpressions.Regex.Replace(item.SourceTitle, @"\s+", "") && e.Phase == ExcelEventParser.NormalizePhase(item.Phase) && e.Start == CalendarRules.ParseDate(item.Start, false)).ToList();
                    if (candidates.Count == 1) source = candidates[0];
                }
                if (source == null || String.IsNullOrWhiteSpace(item.GoogleEventId) || !unique.Add(source.StableId) || !remote.Add(item.GoogleEventId)) throw new InvalidDataException("원본 ID/별칭과 중복 없는 Google 이벤트 ID가 필요합니다.");
                var existing = State.Mappings.FirstOrDefault(m => m.Scope == scope && m.StableId == source.StableId);
                if (State.Mappings.Any(m => m.Scope == scope && m.GoogleEventId == item.GoogleEventId && m.StableId != source.StableId)) throw new InvalidDataException("이 원격 ID는 다른 원본 항목에 이미 연결됐습니다.");
                if (BindingConflict(account, calendar, workbook.DocumentId, scope, source.StableId, item.GoogleEventId)) throw new InvalidDataException("이 계정/캘린더의 다른 원본이 사용하는 원격 ID입니다.");
                if (existing != null && existing.GoogleEventId != item.GoogleEventId) throw new InvalidDataException("이미 연결된 이벤트 ID와 다릅니다. 자동 덮어쓰지 않습니다.");
                pending.Add(new EventMapping { Scope = scope, StableId = source.StableId, GoogleEventId = item.GoogleEventId, AccountId = account, CalendarId = calendar, DocumentId = workbook.DocumentId });
            }
            foreach (var mapping in pending)
            {
                if (!State.Mappings.Any(m => m.Scope == scope && m.StableId == mapping.StableId)) State.Mappings.Add(mapping);
                State.Identities.First(i => i.DocumentId == workbook.DocumentId && i.StableId == mapping.StableId).Confirmed = true;
                workbook.Events.First(e => e.StableId == mapping.StableId).IdentityNeedsReview = false;
            }
            Save();
        }
    }
    public sealed class MappingImport { public int SchemaVersion { get; set; } public List<MappingImportItem> Events { get; set; } }
    public sealed class MappingImportItem { public string StableSourceId { get; set; } public string SourceAlias { get; set; } public string GoogleEventId { get; set; } public string SourceTitle { get; set; } public string Phase { get; set; } public string Start { get; set; } }
    public sealed class RemoteEvent
    {
        public string Id, Summary, ETag, ColorId, Description; public DateTimeOffset Start, End; public bool AllDay, ForbiddenMetadata, HasMeetOrAttendees, HasRecurrence;
        public Dictionary<string, string> PrivateProperties;
        public string Hash { get { return SyncHash.Event(Summary, Start, End, AllDay, ColorId); } }
    }
    public interface ICalendarGateway
    {
        Task<List<RemoteEvent>> List(string calendar, DateTimeOffset from, CancellationToken cancellation);
        Task<RemoteEvent> Get(string calendar, string id, CancellationToken cancellation);
        Task<RemoteEvent> Create(string calendar, string id, Dictionary<string, object> payload, CancellationToken cancellation);
        Task<RemoteEvent> Update(string calendar, string id, string etag, Dictionary<string, object> payload, CancellationToken cancellation);
    }
    public sealed class CalendarApiException : Exception
    {
        public readonly int Status; public CalendarApiException(int status) : base("Calendar API 응답 " + status) { Status = status; }
        public bool Transient { get { return Status == 429 || Status >= 500 || Status == 0; } }
    }
    public sealed class SyncPlanItem
    {
        public SourceEvent Source; public RemoteEvent Remote; public string DesiredHash, GoogleId, ExpectedETag;
        public Dictionary<string, object> Payload;
        public string Action { get; set; } public string Title { get; set; } public string Schedule { get; set; } public string Reason { get; set; }
        public bool NewConfirmationAvailable { get; set; }
        public string StableId { get { return Source == null ? "" : Source.StableId; } }
    }
    public sealed class SyncPlan
    {
        public string Scope, CalendarId, SourceHash, AccountId, AccountEmail, ColorId; public bool RemoteInspected; public WorkbookEvents Workbook; public List<RemoteEvent> RemoteEvents;
        public List<SyncPlanItem> Items = new List<SyncPlanItem>();
    }
    public sealed class SyncEngine
    {
        private readonly SyncRepository repository; public Func<int, CancellationToken, Task> Delay = (n, c) => Task.Delay(250 * n, c);
        public SyncEngine(SyncRepository repository) { this.repository = repository; }
        public static string Scope(string account, string calendar, string document) { return SyncHash.Text("google|" + account + "|" + calendar + "|" + document); }
        public SyncPlan Plan(WorkbookEvents workbook, string account, string calendar, List<RemoteEvent> remote, string color, DateTimeOffset now)
        {
            string scope = Scope(account, calendar, workbook.DocumentId);
            var plan = new SyncPlan { Scope = scope, AccountId = account, ColorId = color, CalendarId = calendar, Workbook = workbook, SourceHash = workbook.FileHash, RemoteInspected = remote != null, RemoteEvents = remote };
            var batchOwners = new Dictionary<string, string>();
            var duplicateSourceIds = new HashSet<string>(workbook.Events.GroupBy(e => e.StableId).Where(g => g.Count() > 1).Select(g => g.Key));
            var cutoff = new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9));
            foreach (var source in workbook.Events.Where(e => (e.End ?? e.Start.AddTicks(1)) > cutoff))
            {
                var item = new SyncPlanItem { Source = source, Title = source.Title, Schedule = CalendarRules.InSeoul(source.Start).ToString("yyyy.MM.dd HH:mm"), Action = "검토", Reason = "" };
                plan.Items.Add(item);
                if (source.UnlinkedSource) { item.Reason = "원본 셀 연결이 없어 Google 동기화를 보류합니다."; continue; }
                if (duplicateSourceIds.Contains(source.StableId)) { item.Reason = "여러 원본 행이 같은 안정 ID를 사용함 · 검토"; continue; }
                var mapping = repository.State.Mappings.FirstOrDefault(m => m.Scope == scope && m.StableId == source.StableId);
                var journal = repository.State.Journal.LastOrDefault(j => j.Scope == scope && j.StableId == source.StableId);
                item.GoogleId = mapping != null ? mapping.GoogleEventId : journal != null ? journal.GoogleEventId : "ca" + SyncHash.Text(scope + "|" + source.StableId);
                if (remote == null) { item.Reason = "원격 미확인 · OAuth 연결 후 신규/변경 확인"; continue; }
                var observed = remote.FirstOrDefault(r => r.Id == item.GoogleId);
                if (mapping == null && observed == null)
                {
                    var candidates = remote.Where(r => !ManualEventService.IsManualId(r.Id) && r.Summary == source.Title && r.Start == source.Start && r.AllDay == source.AllDay).ToList();
                    if (candidates.Count == 1) { observed = candidates[0]; item.GoogleId = observed.Id; }
                    else if (candidates.Count > 1) { item.Reason = "동일 제목/시간의 기존 일정이 여러 개 · ID 매핑 필요"; continue; }
                    else if (remote.Any(r => (r.Summary == source.Title && !SeparateReviewedCycle(source, r, workbook, plan)) || (source.IdentityNeedsReview && r.Start == source.Start))) { item.Reason = "유사한 기존 일정 · 중복 방지를 위해 ID 매핑 필요"; continue; }
                }
                string batchOwner;
                if (ManualEventService.IsManualId(item.GoogleId)) { item.Reason = "위젯에서 직접 등록한 일정은 Excel 원본과 연결하지 않습니다."; continue; }
                if (repository.BindingConflict(account, calendar, workbook.DocumentId, scope, source.StableId, item.GoogleId) || (batchOwners.TryGetValue(item.GoogleId, out batchOwner) && batchOwner != source.StableId))
                { item.Reason = "다른 원본이 사용하는 Google ID · 1:1 매핑 충돌"; continue; }
                batchOwners[item.GoogleId] = source.StableId;
                if (source.UnsafeFormula) { item.Reason = "수식 시간값의 최신성을 알 수 없음"; continue; }
                if (observed != null && observed.ForbiddenMetadata) { item.Reason = "기존 일정에 Meet/참석자/설명 있음 · 검토 필요"; continue; }
                if (mapping != null && observed == null) { item.Reason = "매핑된 원격 ID를 찾지 못함 · 자동 재생성하지 않음"; continue; }
                if (mapping != null && String.IsNullOrEmpty(mapping.LastHash) && observed != null &&
                    (observed.Start != source.Start || (source.BaseTitle == null ? observed.Summary != source.Title : !Normalize(observed.Summary).Contains(Normalize(source.BaseTitle)))))
                { item.Reason = "가져온 ID의 제목/시각이 원본과 다름 · 채택 전 검토"; continue; }
                var end = source.End ?? (observed == null ? (DateTimeOffset?)null : source.Start + (observed.End - observed.Start));
                if (!end.HasValue || end <= source.Start) { item.Reason = "종료/단계 길이를 명시해야 함"; continue; }
                string desiredTitle = source.Title;
                if (observed != null && mapping != null && source.BaseTitle != null && observed.Start == source.Start &&
                    ((String.IsNullOrEmpty(mapping.LastHash) && Normalize(observed.Summary).Contains(Normalize(source.BaseTitle))) || (mapping.LastHash == observed.Hash && mapping.LastSourceTitle == source.Title))) desiredTitle = observed.Summary;
                string desiredColor = String.IsNullOrEmpty(color) && observed != null ? observed.ColorId : color;
                item.DesiredHash = SyncHash.Event(desiredTitle, source.Start, end.Value, source.AllDay, desiredColor);
                item.Payload = source.AllDay ? GooglePayloadPolicy.BuildAllDay(desiredTitle, source.Start.Date, end.Value.Date, color) : GooglePayloadPolicy.Build(desiredTitle, source.Start, end.Value, color);
                item.Remote = observed; item.ExpectedETag = observed == null ? null : observed.ETag;
                if (observed == null)
                {
                    if (source.IdentityNeedsReview && !repository.NewConfirmedForTarget(workbook, source, scope)) { item.NewConfirmationAvailable = true; item.Reason = "처음 등록하는 항목인지 확인 필요 · 같은 일정이 없다면 신규 확인"; continue; }
                    item.Action = "신규"; item.Reason = "기존 일정 없음 · 지정 ID로 생성";
                }
                else if (observed.Hash == item.DesiredHash) { item.Action = mapping == null || String.IsNullOrEmpty(mapping.LastHash) ? "채택" : "유지"; item.Reason = "기존 이벤트 ID 유지"; }
                else if (mapping != null && !String.IsNullOrEmpty(mapping.LastHash) && observed.Hash != mapping.LastHash)
                {
                    item.Action = "충돌"; item.Reason = "원격 일정이 이전 동기화 이후 변경됨";
                }
                else if (mapping == null && (journal == null || journal.State != "pending")) { item.Reason = "기존 일정은 있으나 값이 다름 · 명시 ID 매핑 후 변경 미리보기"; }
                else { item.Action = "변경"; item.Reason = "동일 이벤트 ID에 제목/일정 변경"; }
            }
            foreach (var missing in repository.State.Mappings.Where(m => m.Scope == scope && !workbook.Events.Any(e => e.StableId == m.StableId)))
                plan.Items.Add(new SyncPlanItem { Action = "누락 검토", Title = missing.StableId, Reason = "원본에 없음 · 원격 삭제하지 않음", GoogleId = missing.GoogleEventId });
            return plan;
        }
        private bool SeparateReviewedCycle(SourceEvent source, RemoteEvent remote, WorkbookEvents workbook, SyncPlan plan)
        {
            if (source.IdentityNeedsReview || !source.CycleStart.HasValue || !source.CycleEnd.HasValue || source.CycleEnd <= source.CycleStart) return false;
            var known = plan.Items.FirstOrDefault(i => i.Remote != null && i.Remote.Id == remote.Id && (i.Action == "채택" || i.Action == "유지"));
            if (known == null || known.Source == null || known.Source.StableId == source.StableId || known.Source.Phase != source.Phase || Normalize(known.Source.BaseTitle) != Normalize(source.BaseTitle)) return false;
            var previous = known.Source;
            return previous.CycleStart.HasValue && previous.CycleEnd.HasValue && previous.CycleEnd > previous.CycleStart &&
                (source.CycleStart > previous.CycleEnd || previous.CycleStart > source.CycleEnd);
        }
        public async Task<List<string>> Execute(SyncPlan plan, ICalendarGateway gateway, string currentSourceHash, CancellationToken cancellation)
        {
            if (!plan.RemoteInspected) throw new InvalidOperationException("원격 확인 없이 동기화할 수 없습니다.");
            if (currentSourceHash != plan.SourceHash) throw new InvalidOperationException("원본이 변경됐습니다. 미리보기를 다시 만드세요.");
            var results = new List<string>();
            foreach (var item in plan.Items.Where(i => i.Action == "신규" || i.Action == "변경" || i.Action == "채택" || i.Action == "유지"))
            {
                cancellation.ThrowIfCancellationRequested();
                if (ManualEventService.IsManualId(item.GoogleId)) { results.Add(item.Title + ": 직접 등록 일정 보호"); continue; }
                if (repository.BindingConflict(plan.AccountId, plan.CalendarId, plan.Workbook.DocumentId, plan.Scope, item.StableId, item.GoogleId)) { results.Add(item.Title + ": 1:1 매핑 충돌 · 쓰기하지 않음"); continue; }
                var journal = new SyncJournal { Scope = plan.Scope, StableId = item.StableId, GoogleEventId = item.GoogleId, DesiredHash = item.DesiredHash, Operation = item.Action, State = "pending", AccountId = plan.AccountId, CalendarId = plan.CalendarId, DocumentId = plan.Workbook.DocumentId };
                repository.State.Journal.Add(journal); repository.Save();
                try
                {
                    RemoteEvent response;
                    if (item.Action == "유지" || item.Action == "채택")
                    {
                        response = await gateway.Get(plan.CalendarId, item.GoogleId, cancellation);
                        if (response == null || response.Hash != item.DesiredHash) throw new CalendarApiException(412);
                    }
                    else response = await WriteWithRecovery(plan.CalendarId, item, gateway, cancellation);
                    var mapping = repository.State.Mappings.FirstOrDefault(m => m.Scope == plan.Scope && m.StableId == item.StableId);
                    if (repository.BindingConflict(plan.AccountId, plan.CalendarId, plan.Workbook.DocumentId, plan.Scope, item.StableId, item.GoogleId)) throw new InvalidOperationException("1:1 매핑 저장 충돌");
                    if (mapping == null) { mapping = new EventMapping { Scope = plan.Scope, StableId = item.StableId, GoogleEventId = item.GoogleId, AccountId = plan.AccountId, CalendarId = plan.CalendarId, DocumentId = plan.Workbook.DocumentId }; repository.State.Mappings.Add(mapping); }
                    mapping.AccountId = plan.AccountId; mapping.CalendarId = plan.CalendarId; mapping.DocumentId = plan.Workbook.DocumentId;
                    mapping.LastETag = response.ETag;
                    if (response.ForbiddenMetadata || response.Hash != item.DesiredHash) { journal.State = "response-review"; repository.Save(); results.Add(item.Title + ": 응답 검토 필요 · 재생성하지 않음"); continue; }
                    mapping.LastHash = item.DesiredHash; mapping.LastSourceTitle = item.Source.Title; journal.State = "done";
                    var identity = repository.State.Identities.FirstOrDefault(i => i.DocumentId == plan.Workbook.DocumentId && i.StableId == item.StableId); if (identity != null) identity.Confirmed = true;
                    repository.Save(); results.Add(item.Title + ": " + item.Action + " 완료");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { results.Add(item.Title + ": 실패 · " + (ex is CalendarApiException ? ex.Message : "안전한 재실행을 위해 상태 확인 필요")); }
            }
            return results;
        }
        private async Task<RemoteEvent> WriteWithRecovery(string calendar, SyncPlanItem item, ICalendarGateway gateway, CancellationToken cancellation)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                var current = await gateway.Get(calendar, item.GoogleId, cancellation);
                if (current != null && !current.ForbiddenMetadata && current.Hash == item.DesiredHash) return current;
                if (item.Action == "신규" && current != null) throw new CalendarApiException(409);
                if (item.Action == "변경" && (current == null || current.ETag != item.ExpectedETag || current.ForbiddenMetadata)) throw new CalendarApiException(412);
                CalendarApiException retryFailure = null;
                try
                {
                    return item.Action == "신규" ? await gateway.Create(calendar, item.GoogleId, item.Payload, cancellation) : await gateway.Update(calendar, item.GoogleId, item.ExpectedETag, item.Payload, cancellation);
                }
                catch (CalendarApiException ex)
                {
                    if ((!ex.Transient && ex.Status != 409 && ex.Status != 412) || attempt == 3) throw;
                    retryFailure = ex;
                }
                var recovered = await gateway.Get(calendar, item.GoogleId, cancellation);
                if (recovered != null && !recovered.ForbiddenMetadata && recovered.Hash == item.DesiredHash) return recovered;
                if (retryFailure.Status == 409 || retryFailure.Status == 412) throw retryFailure;
                await Delay(attempt, cancellation);
            }
            throw new CalendarApiException(0);
        }
        private static string Normalize(string value) { return System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", ""); }
    }
}
