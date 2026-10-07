using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    public sealed class ManualEventInput
    {
        public string AccountSubject, CalendarId, Title, StartTime, EndTime;
        public DateTime StartDate, EndDate;
        public bool AllDay;
        public Dictionary<string, string> StatusValues = new Dictionary<string, string>();
        public DateTimeOffset Start, End;
        public void Validate()
        {
            Title = (Title ?? "").Trim();
            if (String.IsNullOrWhiteSpace(AccountSubject) || String.IsNullOrWhiteSpace(CalendarId) || Title.Length == 0 || Title.Length > 200 || Title.Any(Char.IsControl))
                throw new InvalidDataException("계정·캘린더·200자 이내 제목을 확인하세요.");
            if (StartDate.Year < 2000 || EndDate.Year > 2099 || EndDate.Date < StartDate.Date) throw new InvalidDataException("시작일과 종료일을 확인하세요.");
            if (AllDay) {
                Start = new DateTimeOffset(StartDate.Date, TimeSpan.FromHours(9));
                End = new DateTimeOffset(EndDate.Date.AddDays(1), TimeSpan.FromHours(9));
            } else {
                TimeSpan startTime, endTime;
                if (!TimeSpan.TryParseExact(StartTime ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out startTime) ||
                    !TimeSpan.TryParseExact(EndTime ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out endTime) ||
                    startTime.TotalHours >= 24 || endTime.TotalHours >= 24) throw new InvalidDataException("시간은 00:00부터 23:59까지 입력하세요.");
                Start = new DateTimeOffset(StartDate.Date + startTime, TimeSpan.FromHours(9));
                End = new DateTimeOffset(EndDate.Date + endTime, TimeSpan.FromHours(9));
            }
            if (End <= Start || End - Start > TimeSpan.FromDays(366)) throw new InvalidDataException("종료는 시작보다 늦고 기간은 366일 이하여야 합니다.");
            if (StatusValues == null) StatusValues = new Dictionary<string, string>();
            if (StatusValues.Any(v => !GoogleOnlyStatus.Keys.Contains(v.Key) || v.Value == null || v.Value.Length > 256 || v.Value.Any(Char.IsControl)))
                throw new InvalidDataException("상태 값은 각 256자 이내의 일반 텍스트로 입력하세요.");
        }
    }
    public sealed class ManualEventRecord
    {
        public string OperationId { get; set; } public string AccountSubject { get; set; } public string CalendarId { get; set; }
        public string EventId { get; set; } public string Title { get; set; } public DateTimeOffset Start { get; set; } public DateTimeOffset End { get; set; }
        public bool AllDay { get; set; } public string State { get; set; } public string Message { get; set; }
        public Dictionary<string, string> StatusValues { get; set; }
    }
    public sealed class ManualEventStore
    {
        private static readonly object gate = new object();
        private readonly string path;
        public readonly List<ManualEventRecord> Records;
        public ManualEventStore(string path) { this.path = path; Records = File.Exists(path) ? new JavaScriptSerializer().Deserialize<List<ManualEventRecord>>(File.ReadAllText(path)) : new List<ManualEventRecord>(); if (Records == null) throw new InvalidDataException("일정 대기 기록을 읽을 수 없습니다."); }
        public ManualEventRecord GetOrCreate(ManualEventInput input)
        {
            lock (gate) {
                var prior = Records.LastOrDefault(r => r.State != "complete" && r.AccountSubject == input.AccountSubject && r.CalendarId == input.CalendarId &&
                    r.Title == input.Title && r.Start == input.Start && r.End == input.End && r.AllDay == input.AllDay);
                if (prior != null) return prior;
                var created = new ManualEventRecord { OperationId = Guid.NewGuid().ToString("N"), EventId = "aaccafe" + Guid.NewGuid().ToString("N"),
                    AccountSubject = input.AccountSubject, CalendarId = input.CalendarId, Title = input.Title, Start = input.Start, End = input.End,
                    AllDay = input.AllDay, State = "pending", Message = "등록 결과 확인 대기", StatusValues = new Dictionary<string, string>(input.StatusValues) };
                Records.Add(created); Save(); return created;
            }
        }
        public void Save()
        {
            lock (gate) {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp"; File.WriteAllText(temp, JsonData.Serialize(Records));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
        }
    }
    public sealed class ManualEventService
    {
        private readonly ICalendarGateway gateway;
        private readonly Func<CancellationToken, Task<List<RemoteCalendar>>> calendars;
        private readonly ManualEventStore store;
        private readonly bool eventScopeGranted;
        private readonly Func<ManualEventRecord, IStatusEndpoint> statusEndpoint;
        private readonly StatusDraftStore statusDrafts;
        public ManualEventService(ICalendarGateway gateway, Func<CancellationToken, Task<List<RemoteCalendar>>> calendars, ManualEventStore store, bool eventScopeGranted,
            Func<ManualEventRecord, IStatusEndpoint> statusEndpoint = null, StatusDraftStore statusDrafts = null)
        { this.gateway = gateway; this.calendars = calendars; this.store = store; this.eventScopeGranted = eventScopeGranted; this.statusEndpoint = statusEndpoint; this.statusDrafts = statusDrafts; }
        public static bool IsManualId(string id) { return id != null && id.StartsWith("aaccafe", StringComparison.Ordinal) && id.Length == 39; }
        private static bool Matches(RemoteEvent remote, ManualEventRecord record)
        { return remote != null && remote.Id == record.EventId && remote.Summary == record.Title && remote.AllDay == record.AllDay && remote.Start == record.Start && remote.End == record.End; }
        public async Task<ManualEventRecord> CreateOrReconcile(ManualEventInput input, CancellationToken cancellation)
        {
            input.Validate();
            if (!eventScopeGranted) throw new InvalidOperationException("Google 일정 등록 권한을 확인할 수 없습니다.");
            var choices = await calendars(cancellation);
            if (choices.Count(c => c.Id == input.CalendarId && CalendarAccess.CanWrite(c)) != 1) throw new InvalidOperationException("선택한 캘린더의 쓰기 권한을 확인할 수 없습니다.");
            var record = store.GetOrCreate(input);
            if (record.State == "complete") return record;
            try {
                var existing = await gateway.Get(record.CalendarId, record.EventId, cancellation);
                if (existing != null) {
                    if (!Matches(existing, record)) throw new StatusConflictException("등록 ID에 다른 일정이 있습니다.");
                    return await FinishStatus(record, existing, cancellation);
                }
                var payload = record.AllDay ? GooglePayloadPolicy.BuildAllDay(record.Title, record.Start.Date, record.End.Date, "") :
                    GooglePayloadPolicy.Build(record.Title, record.Start, record.End, "");
                await gateway.Create(record.CalendarId, record.EventId, payload, cancellation);
            } catch (StatusConflictException) { record.State = "conflict"; record.Message = "같은 ID의 다른 일정 확인 필요"; store.Save(); return record; }
            catch (OperationCanceledException) { record.State = "pending"; record.Message = "등록 결과가 불확실합니다. 같은 ID로 다시 확인하세요."; store.Save(); return record; }
            catch { /* A timed-out create may have succeeded. The fixed ID is reconciled below. */ }
            try {
                var observed = await gateway.Get(record.CalendarId, record.EventId, cancellation);
                if (Matches(observed, record)) return await FinishStatus(record, observed, cancellation);
                else if (observed != null) { record.State = "conflict"; record.Message = "같은 ID의 다른 일정 확인 필요"; }
                else { record.State = "pending"; record.Message = "Google 등록 결과 미확인 · 동일 ID 재시도 필요"; }
            } catch { record.State = "pending"; record.Message = "Google 등록 결과 미확인 · 동일 ID 재시도 필요"; }
            store.Save(); return record;
        }
        private async Task<ManualEventRecord> FinishStatus(ManualEventRecord record, RemoteEvent observed, CancellationToken cancellation)
        {
            if (record.StatusValues == null || !record.StatusValues.Any(v => !String.IsNullOrWhiteSpace(v.Value))) {
                record.State = "complete"; record.Message = "Google 일정 등록 확인됨"; store.Save(); return record;
            }
            if (statusEndpoint == null || statusDrafts == null) { record.State = "pending"; record.Message = "일정 등록됨 · 상태 반영 대기"; store.Save(); return record; }
            var context = GoogleOnlyStatus.Context(record.AccountSubject, record.CalendarId, observed);
            if (context == null) { record.State = "conflict"; record.Message = "일정은 등록됐지만 상태 블록을 확인해야 합니다."; store.Save(); return record; }
            var draft = context.CreateDraft();
            foreach (var item in record.StatusValues) draft.Values[item.Key] = item.Value;
            var previous = statusDrafts.Drafts.LastOrDefault(d => d.Key == draft.Key && !d.Superseded && d.State != "complete");
            if (previous != null) draft = previous; else statusDrafts.Put(draft);
            await new GoogleOnlyStatusSaveCoordinator(statusDrafts).Save(draft, statusEndpoint(record), cancellation);
            record.State = draft.State == "complete" ? "complete" : draft.State == "conflict" ? "conflict" : "pending";
            record.Message = draft.State == "complete" ? "Google 일정과 상태 등록 확인됨" : "일정은 등록됐지만 상태 반영 대기 · 같은 일정 ID로 다시 확인하세요.";
            store.Save(); return record;
        }
    }
}
