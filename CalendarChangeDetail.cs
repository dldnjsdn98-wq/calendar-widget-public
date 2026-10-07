using System;
using System.Collections.Generic;

namespace CalendarWidget
{
    public sealed class CalendarChangeField
    {
        public string Label { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
    }
    public sealed class CalendarChangeEntry
    {
        public string CalendarName { get; set; }
        public string EventName { get; set; }
        public string Kind { get; set; }
        public DateTimeOffset DetectedAt { get; set; }
        public List<CalendarChangeField> Fields { get; set; }
        public string Headline { get { return Kind + " · " + EventName + " · " + CalendarName; } }
    }
    public static class CalendarChangeDetail
    {
        private static string Value(CalendarEvent item, string key)
        {
            if (item == null) return "—";
            if (key == "title") return item.Title ?? "";
            if (key == "start" || key == "end") {
                string raw = key == "start" ? item.Start : item.End;
                if (item.AllDay) {
                    DateTime date;
                    return DateTime.TryParse(raw, out date) ? (key == "end" ? date.AddDays(-1) : date).ToString("yyyy.MM.dd") : raw ?? "";
                }
                DateTimeOffset when;
                return DateTimeOffset.TryParse(raw, out when) ? CalendarRules.InSeoul(when).ToString("yyyy.MM.dd HH:mm") + " (한국 시간)" : raw ?? "";
            }
            if (key == "allDay") return item.AllDay ? "종일" : "시간 지정";
            if (key == "issue") return item.RemoteStatusIssue ?? "";
            string value;
            return item.RemoteStatus != null && item.RemoteStatus.TryGetValue(key, out value) ? value ?? "" : "확인 불가";
        }
        public static CalendarChangeEntry Create(CalendarProfile calendar, CalendarEvent before, CalendarEvent after, DateTimeOffset now, bool duplicateName)
        {
            var entry = new CalendarChangeEntry {
                CalendarName = calendar.Name + (duplicateName ? " (" + (calendar.AccountName ?? calendar.AccountId ?? calendar.Id) + ")" : ""),
                EventName = (after ?? before).Title ?? "(제목 없음)", Kind = before == null ? "추가" : after == null ? "취소" : "수정",
                DetectedAt = now, Fields = new List<CalendarChangeField>()
            };
            var keys = new[] { "title", "start", "end", "allDay", "setup", "alphaCheck", "alphaTester", "realCheck", "realTester", "issue" };
            var labels = new[] { "일정 이름", "시작", "종료", "시간 종류", "세팅", "확인 · 알파", "알파와 테스터 · 테스터", "확인 · 리얼", "리얼과 테스터 · 테스터", "Google 상태 문제" };
            for (int i = 0; i < keys.Length; i++) {
                string oldValue = Value(before, keys[i]), newValue = Value(after, keys[i]);
                if (oldValue != newValue || before == null || after == null)
                    entry.Fields.Add(new CalendarChangeField { Label = labels[i], Before = oldValue, After = newValue });
            }
            return entry;
        }
    }
}
