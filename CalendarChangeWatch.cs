using System;
using System.Collections.Generic;
using System.Linq;

namespace CalendarWidget
{
    public static class RemoteStatusProjection
    {
        public static Dictionary<string, string> Read(RemoteEvent item, out string issue, out string binding)
        {
            issue = null; binding = null;
            var privateValues = item.PrivateProperties ?? new Dictionary<string, string>();
            string version;
            bool hasPrivate = privateValues.TryGetValue("cw.status.version", out version);
            bool hasBlock = (item.Description ?? "").Contains(StatusPayloadPolicy.BeginMarker);
            if (hasPrivate) {
                if (version != "1") { issue = "Google 상태 형식을 확인할 수 없습니다."; return null; }
                privateValues.TryGetValue("cw.status.binding", out binding);
                var values = GoogleOnlyStatus.Keys.ToDictionary(key => key, key => privateValues.ContainsKey("cw.status." + key) ? privateValues["cw.status." + key] : "");
                if (hasBlock) {
                    Dictionary<string, string> parsed;
                    if (!StatusPayloadPolicy.TryReadDescription(item.Description, out parsed) ||
                        GoogleOnlyStatus.Keys.Any(key => !parsed.ContainsKey(key) || parsed[key] != values[key]))
                        issue = "Google 설명의 상태와 저장된 상태가 다릅니다.";
                }
                return values;
            }
            if (!hasBlock) return null;
            Dictionary<string, string> fromDescription;
            if (!StatusPayloadPolicy.TryReadDescription(item.Description, out fromDescription)) {
                issue = "Google 설명의 상태 형식을 확인할 수 없습니다."; return null;
            }
            return GoogleOnlyStatus.Keys.ToDictionary(key => key, key => fromDescription.ContainsKey(key) ? fromDescription[key] : "");
        }
    }
    public sealed class CalendarChangeSummary
    {
        public int Added, Updated, Removed, Conflicts;
        public readonly List<CalendarChangeEntry> Entries = new List<CalendarChangeEntry>();
        public int Total { get { return Added + Updated + Removed; } }
        public void Append(CalendarChangeSummary other)
        {
            if (other == null) return;
            Added += other.Added; Updated += other.Updated; Removed += other.Removed;
            Conflicts = other.Conflicts; Entries.AddRange(other.Entries);
        }
        public string Text { get { return "일정 변경: 추가 " + Added + " · 수정 " + Updated + " · 취소 " + Removed + (Conflicts > 0 ? " · 상태 차이 " + Conflicts : ""); } }
    }
    public sealed class CalendarChangeTracker
    {
        private readonly Dictionary<string, Dictionary<string, CalendarEvent>> baselines = new Dictionary<string, Dictionary<string, CalendarEvent>>();
        private readonly Dictionary<string, DateTimeOffset> suppressUntil = new Dictionary<string, DateTimeOffset>();
        public DateTimeOffset? LastSuccessUtc { get; private set; }
        public void Suppress(string eventKey, DateTimeOffset now)
        {
            if (!String.IsNullOrWhiteSpace(eventKey)) suppressUntil[eventKey] = now.AddMinutes(15);
        }
        public CalendarChangeSummary Accept(CalendarSnapshot snapshot, DateTimeOffset now)
        {
            var result = new CalendarChangeSummary();
            foreach (var key in suppressUntil.Where(pair => pair.Value < now).Select(pair => pair.Key).ToList()) suppressUntil.Remove(key);
            foreach (var calendar in snapshot.Calendars.Where(c => c.ReadSucceeded)) {
                var current = snapshot.Events.Where(e => e.CalendarId == calendar.Id && e.Origin != "excel-unlinked").ToDictionary(e => e.Id);
                Dictionary<string, CalendarEvent> previous;
                if (baselines.TryGetValue(calendar.Id, out previous)) {
                    foreach (var item in current) {
                        CalendarEvent old;
                        if (!previous.TryGetValue(item.Key, out old)) { if (!SuppressDelta(item.Key, now)) { result.Added++; result.Entries.Add(CalendarChangeDetail.Create(calendar, null, item.Value, now, snapshot.Calendars.Count(c => c.Name == calendar.Name) > 1)); } }
                        else if (Fingerprint(old) != Fingerprint(item.Value) && !SuppressDelta(item.Key, now)) { result.Updated++; result.Entries.Add(CalendarChangeDetail.Create(calendar, old, item.Value, now, snapshot.Calendars.Count(c => c.Name == calendar.Name) > 1)); }
                    }
                    foreach (var old in previous) {
                        if (current.ContainsKey(old.Key)) continue;
                        try {
                            var end = CalendarRules.ParseDate(old.Value.IsPoint ? old.Value.Start : old.Value.End, old.Value.AllDay);
                            if (end > new DateTimeOffset(CalendarRules.Cutoff(now), TimeSpan.FromHours(9)) && !SuppressDelta(old.Key, now)) { result.Removed++; result.Entries.Add(CalendarChangeDetail.Create(calendar, old.Value, null, now, snapshot.Calendars.Count(c => c.Name == calendar.Name) > 1)); }
                        } catch { }
                    }
                }
                baselines[calendar.Id] = current;
            }
            result.Conflicts = snapshot.Events.Count(e => !String.IsNullOrEmpty(e.StatusConflict));
            LastSuccessUtc = now;
            return result;
        }
        private bool SuppressDelta(string key, DateTimeOffset now)
        {
            DateTimeOffset until;
            if (!suppressUntil.TryGetValue(key, out until)) return false;
            suppressUntil.Remove(key);
            return until >= now;
        }
        private static string Fingerprint(CalendarEvent item)
        {
            return SyncHash.Text(JsonData.Serialize(new {
                item.Title, item.Start, item.End, item.AllDay, item.RemoteStatusIssue,
                remoteStatus = item.RemoteStatus == null ? null : item.RemoteStatus.OrderBy(v => v.Key).Select(v => v.Key + "=" + v.Value).ToArray()
            }));
        }
    }
    public sealed class CalendarPollBackoff
    {
        public int Failures { get; private set; }
        public DateTimeOffset NextAttemptUtc { get; private set; }
        public void Succeeded(DateTimeOffset now) { Failures = 0; NextAttemptUtc = now.AddMinutes(5); }
        public void Failed(DateTimeOffset now) { Failures++; NextAttemptUtc = now.AddMinutes(Math.Min(30, 5 * (1 << Math.Min(Failures - 1, 3)))); }
        public bool Ready(DateTimeOffset now) { return now >= NextAttemptUtc; }
    }
}
