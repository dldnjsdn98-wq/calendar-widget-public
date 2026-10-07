using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace CalendarWidget
{
    // Pure allowlist for the future direct API writer. No network/authentication here.
    public static class GooglePayloadPolicy
    {
        public static void ValidateWrite(Dictionary<string, object> payload)
        {
            if (payload == null || payload.Keys.Any(k => k != "summary" && k != "start" && k != "end" && k != "colorId") || !payload.ContainsKey("summary") || !payload.ContainsKey("start") || !payload.ContainsKey("end")) throw new InvalidOperationException("허용된 일정 필드만 전송할 수 있습니다.");
            foreach (var key in new[] { "start", "end" })
            {
                var value = payload[key] as Dictionary<string, object>;
                if (value == null || value.Keys.Any(k => k != "date" && k != "dateTime" && k != "timeZone")) throw new InvalidOperationException("일정 시간 필드 형식 오류");
            }
        }
        public static Dictionary<string, object> BuildAllDay(string title, DateTime start, DateTime end, string colorId)
        {
            if (String.IsNullOrWhiteSpace(title) || end <= start) throw new ArgumentException("종일 일정 종료일은 시작일보다 뒤여야 합니다.");
            var payload = new Dictionary<string, object> { { "summary", title }, { "start", new Dictionary<string, object> { { "date", start.ToString("yyyy-MM-dd") } } }, { "end", new Dictionary<string, object> { { "date", end.ToString("yyyy-MM-dd") } } } };
            if (!String.IsNullOrEmpty(colorId)) payload["colorId"] = colorId; return payload;
        }
        public static Dictionary<string, object> Build(string title, DateTimeOffset start, DateTimeOffset end, string colorId)
        {
            if (String.IsNullOrWhiteSpace(title) || end <= start) throw new ArgumentException("명시한 제목과 시작/종료 시간이 필요합니다.");
            var payload = new Dictionary<string, object> {
                { "summary", title },
                { "start", new Dictionary<string, object> { { "dateTime", start.ToString("o") }, { "timeZone", "Asia/Seoul" } } },
                { "end", new Dictionary<string, object> { { "dateTime", end.ToString("o") }, { "timeZone", "Asia/Seoul" } } }
            };
            if (!String.IsNullOrEmpty(colorId)) payload["colorId"] = colorId;
            return payload;
        }
        public static bool ResponseNeedsReview(Dictionary<string, object> response)
        {
            object value;
            if (response.TryGetValue("hangoutLink", out value) && value != null && value.ToString().Length > 0) return true;
            if (response.TryGetValue("conferenceData", out value) && value != null) return true;
            if (response.TryGetValue("attendees", out value) && value != null)
            {
                var list = value as ICollection;
                if (list == null || list.Count > 0) return true;
            }
            // This widget intentionally sends no description. Unexpected text is reviewed.
            if (response.TryGetValue("description", out value) && value != null && value.ToString().Length > 0) return true;
            return false;
        }
    }
}
