using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace CalendarWidget
{
    public sealed class FieldDefinition { public string Key { get; set; } public string Label { get; set; } }
    public sealed class CalendarProfile
    {
        public string Id { get; set; } public string Name { get; set; } public string Color { get; set; }
        public bool ReadSucceeded { get; set; }
        public List<FieldDefinition> Fields { get; set; }
        public string AccountId { get; set; } public string RemoteCalendarId { get; set; } public string AccountName { get; set; } public string AccessRole { get; set; }
    }
    public sealed class CalendarChoiceOption
    {
        public string Id { get; set; } public string Name { get; set; } public bool IsSelected { get; set; }
    }
    public sealed class CalendarEvent
    {
        public string Id { get; set; } public string CalendarId { get; set; } public string Title { get; set; }
        public string Start { get; set; } public string End { get; set; } public bool AllDay { get; set; }
        public Dictionary<string, string> RemoteStatus { get; set; }
        public string RemoteStatusIssue { get; set; }
        public string StatusConflict { get; set; }
        public Dictionary<string, string> Fields { get; set; }
        public bool IsPoint { get; set; }
        public StatusEditContext StatusContext { get; set; }
        public string Origin { get; set; }
    }
    public sealed class CalendarSnapshot
    {
        public int SchemaVersion { get; set; } public string Mode { get; set; } public string TimeZone { get; set; }
        public List<CalendarProfile> Calendars { get; set; } public List<CalendarEvent> Events { get; set; }
        public string ObservedAt { get; set; }
        public string Scope { get; set; }
        public List<string> Warnings { get; set; }
    }
    // Future read-only providers return this same model. UI does not fetch remote data.
    public interface ICalendarSource { CalendarSnapshot Load(); }
    public sealed class LocalSampleSource : ICalendarSource
    {
        private readonly string path;
        public LocalSampleSource(string path) { this.path = path; }
        public CalendarSnapshot Load()
        {
            var text = File.ReadAllText(path);
            if (text.Length > 1000000) throw new InvalidDataException("샘플 파일이 너무 큽니다.");
            var snapshot = new JavaScriptSerializer().Deserialize<CalendarSnapshot>(text);
            CalendarRules.Validate(snapshot);
            return snapshot;
        }
    }
    public sealed class FieldView { public string Label { get; set; } public string Value { get; set; } }
    public sealed class EventView
    {
        public string Id { get; set; } public StatusEditContext StatusContext { get; set; }
        public string Origin { get; set; }
        public bool CanEdit { get { return StatusContext != null && !StatusContext.ReadOnly && StatusContext.Binding != null && StatusContext.Binding.Exact; } }
        public string EditHint { get { return CanEdit ? "원본 행이 확인된 상태 수정 · 온라인 연결 전에는 보류함에 저장" : "회차·단계·상태 행을 정확히 연결해야 수정할 수 있습니다."; } }
        public string PendingStatus { get; set; }
        public string StatusConflict { get; set; }
        public string CalendarId { get; set; }
        public string Title { get; set; } public string Schedule { get; set; } public string CalendarName { get; set; }
        public string RelativeLabel { get; set; } public Brush Color { get; set; } public List<FieldView> Fields { get; set; }
    }
    public static class CalendarRules
    {
        public static readonly DateTime MinimumDate = new DateTime(2026, 10, 2);
        public static readonly TimeZoneInfo Seoul = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        public static DateTimeOffset InSeoul(DateTimeOffset value) { return TimeZoneInfo.ConvertTime(value, Seoul); }
        public static DateTime Cutoff(DateTimeOffset now) { var day = InSeoul(now).Date; return day < MinimumDate ? MinimumDate : day; }
        public static DateTimeOffset ParseDate(string value, bool allDay)
        {
            if (allDay)
            {
                var day = DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), TimeSpan.FromHours(9));
            }
            if (String.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"(Z|[+-]\d{2}:\d{2})$"))
                throw new InvalidDataException("시간 일정에는 UTC 또는 시간대 오프셋이 필요합니다.");
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
        }
        public static void Validate(CalendarSnapshot data)
        {
            if (data == null || data.SchemaVersion != 1 || !new[] { "sample", "local-xlsx", "sharepoint-snapshot", "synthetic", "google", "google-loading" }.Contains(data.Mode) || data.TimeZone != "Asia/Seoul")
                throw new InvalidDataException("지원되는 일정 모델과 Asia/Seoul 데이터가 필요합니다.");
            if (data.Calendars == null || data.Events == null || data.Calendars.Count == 0)
                throw new InvalidDataException("캘린더 목록과 일정 목록이 필요합니다.");
            var ids = new HashSet<string>();
            foreach (var calendar in data.Calendars)
            {
                if (String.IsNullOrWhiteSpace(calendar.Id) || String.IsNullOrWhiteSpace(calendar.Name) || !ids.Add(calendar.Id))
                    throw new InvalidDataException("캘린더 이름과 고유 ID를 확인하세요.");
                if (calendar.Color == null || !Regex.IsMatch(calendar.Color, @"^#[0-9a-fA-F]{6}$"))
                    throw new InvalidDataException("캘린더 색은 #RRGGBB 형식이어야 합니다.");
                if (calendar.Fields == null) calendar.Fields = new List<FieldDefinition>();
                var keys = new HashSet<string>();
                foreach (var field in calendar.Fields)
                    if (field == null || String.IsNullOrWhiteSpace(field.Key) || String.IsNullOrWhiteSpace(field.Label) || !keys.Add(field.Key))
                        throw new InvalidDataException("추가 필드에는 고유 키와 표시 이름이 필요합니다.");
            }
            var eventIds = new HashSet<string>();
            foreach (var ev in data.Events)
            {
                if (ev == null || String.IsNullOrWhiteSpace(ev.Id) || !eventIds.Add(ev.Id) || !ids.Contains(ev.CalendarId) || String.IsNullOrWhiteSpace(ev.Title))
                    throw new InvalidDataException("일정 ID, 캘린더 ID, 이벤트명을 확인하세요.");
                if (!ev.IsPoint && ParseDate(ev.End, ev.AllDay) <= ParseDate(ev.Start, ev.AllDay))
                    throw new InvalidDataException("일정 종료는 시작보다 뒤여야 합니다.");
                if (ev.Fields == null) ev.Fields = new Dictionary<string, string>();
            }
        }
        public static List<EventView> Project(CalendarSnapshot data, string calendarId, DateTimeOffset now)
        {
            var cutoff = new DateTimeOffset(Cutoff(now), TimeSpan.FromHours(9));
            return data.Events.Where(ev => (calendarId == null || calendarId == ev.CalendarId) && (ev.IsPoint ? ParseDate(ev.Start, false) >= cutoff : ParseDate(ev.End, ev.AllDay) > cutoff))
                .OrderBy(ev => ParseDate(ev.Start, ev.AllDay)).Select(ev => View(data, ev, now)).ToList();
        }
        public static List<EventView> ProjectDate(CalendarSnapshot data, string calendarId, DateTime date, DateTimeOffset now)
        {
            return data.Events.Where(ev => (calendarId == null || calendarId == ev.CalendarId) && MonthCalendar.Occurs(ev, date))
                .OrderBy(ev => ParseDate(ev.Start, ev.AllDay)).Select(ev => View(data, ev, now)).ToList();
        }
        private static EventView View(CalendarSnapshot data, CalendarEvent ev, DateTimeOffset now)
        {
            var calendar = data.Calendars.First(c => c.Id == ev.CalendarId);
            var start = InSeoul(ParseDate(ev.Start, ev.AllDay)); var end = ev.IsPoint ? start : InSeoul(ParseDate(ev.End, ev.AllDay));
            string schedule;
            if (ev.IsPoint) schedule = start.ToString("MM.dd (ddd) HH:mm", CultureInfo.GetCultureInfo("ko-KR")) + " · 단계 시각";
            else if (ev.AllDay)
            {
                // Google Calendar all-day end dates are exclusive.
                var last = end.AddDays(-1);
                schedule = start.ToString("MM.dd (ddd)", CultureInfo.GetCultureInfo("ko-KR")) +
                    (start.Date == last.Date ? "" : " – " + last.ToString("MM.dd (ddd)", CultureInfo.GetCultureInfo("ko-KR"))) + " · 종일";
            }
            else schedule = start.ToString("MM.dd (ddd) HH:mm", CultureInfo.GetCultureInfo("ko-KR")) + " – " +
                end.ToString(start.Date == end.Date ? "HH:mm" : "MM.dd (ddd) HH:mm", CultureInfo.GetCultureInfo("ko-KR"));
            var days = (start.Date - InSeoul(now).Date).Days;
            var label = now >= start && now < end ? "진행 중" : days == 0 ? "오늘" : days > 0 ? "D-" + days : "오늘 종료";
            var nativeWithStatus = ev.Origin == "google-direct" && ev.StatusContext != null && ev.StatusContext.HasRemoteStatus;
            var fields = (ev.Origin == "google-direct" && !nativeWithStatus ? new List<FieldDefinition>() :
                ev.Origin == "google-direct" ? ConnectedCalendarSource.StatusFields() : calendar.Fields).Select(f => new FieldView { Label = f.Label,
                Value = ev.Fields.ContainsKey(f.Key) && !String.IsNullOrWhiteSpace(ev.Fields[f.Key]) ? ev.Fields[f.Key] : "미정" }).ToList();
            return new EventView { Id = ev.Id, StatusContext = ev.StatusContext, Origin = ev.Origin, CalendarId = calendar.Id, Title = ev.Title, Schedule = schedule, CalendarName = calendar.Name + (data.Mode == "sample" || data.Mode == "synthetic" ? " · 샘플" : data.Mode == "google" || data.Mode == "google-loading" ? " · Google 연결" : " · 원본 읽기"), RelativeLabel = label,
                Color = (Brush)new BrushConverter().ConvertFromString(calendar.Color), Fields = fields, StatusConflict = ev.StatusConflict };
        }
    }
}
