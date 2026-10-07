using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace CalendarWidget
{
    public sealed class MonthEventMark { public string Title { get; set; } public Brush Color { get; set; } }
    public sealed class MonthDay
    {
        public DateTime Date { get; set; } public string Number { get { return Date.Day.ToString(CultureInfo.InvariantCulture); } }
        public bool InMonth { get; set; } public bool IsToday { get; set; } public bool Selected { get; set; }
        public bool Dark { get; set; }
        public Brush Background { get { return System.Windows.SystemParameters.HighContrast ? (Selected ? System.Windows.SystemColors.HighlightBrush : System.Windows.SystemColors.WindowBrush) : BrushOf(Selected ? "#6D3CCB" : Dark ? "#24222B" : "#FFFFFF"); } }
        public Brush Foreground { get { return System.Windows.SystemParameters.HighContrast ? (Selected ? System.Windows.SystemColors.HighlightTextBrush : System.Windows.SystemColors.WindowTextBrush) : BrushOf(Selected ? "#FFFFFF" : InMonth ? Dark ? "#F6F2FA" : "#262230" : Dark ? "#AEA5B8" : "#AAA4B2"); } }
        public Brush Border { get { return System.Windows.SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush : BrushOf(IsToday && !Selected ? Dark ? "#C09BFF" : "#6D3CCB" : Dark ? "#62596C" : "#E8E3EF"); } }
        public int BorderSize { get { return IsToday && !Selected ? 2 : 0; } }
        public string CountLabel { get { return EventCount == 0 ? "" : EventCount.ToString(CultureInfo.InvariantCulture); } }
        public int EventCount { get; set; }
        public List<MonthEventMark> Marks { get; set; }
        public string Overflow { get; set; }
        private static Brush BrushOf(string text) { return (Brush)new BrushConverter().ConvertFromString(text); }
    }
    public static class MonthCalendar
    {
        public static List<MonthDay> Build(CalendarSnapshot snapshot, string calendarId, DateTime displayedMonth, DateTime today, DateTime? selected, bool dark = false)
        {
            var first = new DateTime(displayedMonth.Year, displayedMonth.Month, 1);
            var start = first.AddDays(-(int)first.DayOfWeek);
            var events = snapshot.Events.Where(e => calendarId == null || e.CalendarId == calendarId).ToList();
            var result = new List<MonthDay>();
            for (int i = 0; i < 42; i++) {
                var date = start.AddDays(i);
                var matches = events.Where(e => Occurs(e, date)).OrderBy(e => CalendarRules.ParseDate(e.Start, e.AllDay)).ThenBy(e => e.Title).ToList();
                result.Add(new MonthDay { Date = date, InMonth = date.Month == first.Month, IsToday = date == today.Date, Selected = selected.HasValue && selected.Value.Date == date, Dark = dark,
                    EventCount = matches.Count,
                    Marks = matches.Take(3).Select(e => new MonthEventMark { Title = e.Title, Color = (Brush)new BrushConverter().ConvertFromString(snapshot.Calendars.Single(c => c.Id == e.CalendarId).Color) }).ToList(),
                    Overflow = matches.Count > 2 ? "+" + (matches.Count - 2).ToString(CultureInfo.InvariantCulture) : "" });
            }
            return result;
        }
        public static bool Occurs(CalendarEvent item, DateTime date)
        {
            var start = CalendarRules.InSeoul(CalendarRules.ParseDate(item.Start, item.AllDay)).Date;
            if (item.IsPoint) return start == date.Date;
            var end = CalendarRules.InSeoul(CalendarRules.ParseDate(item.End, item.AllDay));
            // All-day and timed events both use an exclusive end. A timed event ending after midnight occupies that day.
            var last = end.TimeOfDay == TimeSpan.Zero ? end.Date.AddDays(-1) : end.Date;
            return date.Date >= start && date.Date <= last;
        }
        public static DateTime Move(DateTime displayedMonth, int delta)
        {
            return new DateTime(displayedMonth.Year, displayedMonth.Month, 1).AddMonths(delta);
        }
    }
}
