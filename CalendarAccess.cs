using System;
using System.Collections.Generic;
using System.Linq;

namespace CalendarWidget
{
    public static class CalendarAccess
    {
        public static bool HasEventWriteScope(string scopes)
        {
            return (scopes ?? "").Split(' ').Contains("https://www.googleapis.com/auth/calendar.events", StringComparer.Ordinal);
        }
        public static bool CanRead(RemoteCalendar calendar)
        {
            return calendar != null && !String.IsNullOrEmpty(calendar.Id) &&
                (calendar.AccessRole == "owner" || calendar.AccessRole == "writer" || calendar.AccessRole == "reader");
        }
        public static bool CanWrite(RemoteCalendar calendar)
        {
            return calendar != null && !String.IsNullOrEmpty(calendar.Id) &&
                (calendar.AccessRole == "owner" || calendar.AccessRole == "writer");
        }
        public static List<string> SelectedIds(AccountConfiguration account)
        {
            var ids = new List<string>();
            if (!String.IsNullOrWhiteSpace(account.TargetCalendarId)) ids.Add(account.TargetCalendarId);
            if (account.CalendarIds != null) ids.AddRange(account.CalendarIds.Where(id => !String.IsNullOrWhiteSpace(id)));
            if (account.NativeCalendarIds != null) ids.AddRange(account.NativeCalendarIds.Where(id => !String.IsNullOrWhiteSpace(id)));
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}
