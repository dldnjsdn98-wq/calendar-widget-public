using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public sealed class RemoteCalendar
    {
        public string Id { get; set; } public string Name { get; set; } public string AccessRole { get; set; } public string Color { get; set; }
        public override string ToString() { return Name; }
    }
    public sealed class GoogleGateway : ICalendarGateway
    {
        private readonly IHttpTransport transport; private readonly OAuthService auth; private readonly OAuthClientSettings client; private readonly string key; private readonly bool approved, allowRefresh;
        public GoogleGateway(IHttpTransport transport, OAuthService auth, OAuthClientSettings client, string key, bool approved, bool allowRefresh = true) { this.transport = transport; this.auth = auth; this.client = client; this.key = key; this.approved = approved; this.allowRefresh = allowRefresh; }
        internal async Task<Dictionary<string, object>> Request(string method, string path, Dictionary<string, object> payload, string etag, CancellationToken cancellation)
        {
            bool forceRefresh = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var token = await auth.AccessToken(client, key, approved, cancellation, forceRefresh, allowRefresh); forceRefresh = false;
                HttpReply reply = null; CalendarApiException transportError = null;
                try { reply = await transport.Send(new HttpRequestData { Method = method, Url = "https://www.googleapis.com/calendar/v3/" + path, Bearer = token, Body = payload == null ? null : JsonData.Serialize(payload), ETag = etag }, cancellation); }
                catch (CalendarApiException ex) { transportError = ex; }
                if (transportError != null) { if (method != "GET" || !transportError.Transient || attempt == 2) throw transportError; await Task.Delay(200 * (attempt + 1), cancellation); continue; }
                if (reply.Status == 401 && attempt == 0 && allowRefresh) { forceRefresh = true; continue; }
                if (method == "GET" && (reply.Status == 429 || reply.Status >= 500) && attempt < 2) { await Task.Delay(200 * (attempt + 1), cancellation); continue; }
                if (reply.Status == 404) return null;
                if (reply.Status < 200 || reply.Status > 299) throw new CalendarApiException(reply.Status);
                return JsonData.Parse(reply.Body);
            }
            throw new CalendarApiException(0);
        }
        public async Task<List<RemoteCalendar>> Calendars(CancellationToken cancellation)
        {
            var list = new List<RemoteCalendar>(); string page = null;
            for (int i = 0; i < 50; i++)
            {
                var data = await Request("GET", "users/me/calendarList?maxResults=250" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, null, cancellation);
                foreach (var item in JsonData.Array(data, "items").OfType<Dictionary<string, object>>()) list.Add(new RemoteCalendar { Id = JsonData.String(item, "id"), Name = JsonData.String(item, "summary"), AccessRole = JsonData.String(item, "accessRole"), Color = JsonData.String(item, "backgroundColor") });
                page = JsonData.String(data, "nextPageToken"); if (page == null) return list;
            }
            throw new InvalidDataException("캘린더 페이지 제한 초과 · 일부 목록으로 진행하지 않습니다.");
        }
        public async Task<List<RemoteEvent>> List(string calendar, DateTimeOffset from, CancellationToken cancellation)
        {
            var list = new List<RemoteEvent>(); string page = null;
            for (int i = 0; i < 50; i++)
            {
                var data = await Request("GET", "calendars/" + Uri.EscapeDataString(calendar) + "/events?singleEvents=true&maxResults=2500&timeMin=" + Uri.EscapeDataString(from.ToString("o")) + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, null, cancellation);
                if (data == null) throw new InvalidDataException("대상 캘린더를 찾지 못했습니다.");
                foreach (var item in JsonData.Array(data, "items").OfType<Dictionary<string, object>>()) { if (JsonData.String(item, "status") != "cancelled") list.Add(ParseEvent(item)); }
                page = JsonData.String(data, "nextPageToken"); if (page == null) return list;
            }
            throw new InvalidDataException("일정 페이지 제한 초과 · 일부 목록으로 동기화하지 않습니다.");
        }
        public async Task<RemoteEvent> Get(string calendar, string id, CancellationToken cancellation)
        {
            var data = await Request("GET", "calendars/" + Uri.EscapeDataString(calendar) + "/events/" + Uri.EscapeDataString(id), null, null, cancellation);
            return data == null || JsonData.String(data, "status") == "cancelled" ? null : ParseEvent(data);
        }
        public async Task<RemoteEvent> Create(string calendar, string id, Dictionary<string, object> payload, CancellationToken cancellation)
        {
            GooglePayloadPolicy.ValidateWrite(payload);
            var allowed = new Dictionary<string, object>(payload); allowed["id"] = id;
            allowed["transparency"] = "transparent";
            allowed["reminders"] = new Dictionary<string, object> { { "useDefault", false }, { "overrides", new object[0] } };
            return ParseEvent(await Request("POST", "calendars/" + Uri.EscapeDataString(calendar) + "/events?conferenceDataVersion=0&sendUpdates=none", allowed, null, cancellation));
        }
        public async Task<RemoteEvent> Update(string calendar, string id, string etag, Dictionary<string, object> payload, CancellationToken cancellation)
        {
            GooglePayloadPolicy.ValidateWrite(payload);
            return ParseEvent(await Request("PATCH", "calendars/" + Uri.EscapeDataString(calendar) + "/events/" + Uri.EscapeDataString(id) + "?conferenceDataVersion=0&sendUpdates=none", payload, etag, cancellation));
        }
        public static RemoteEvent ParseEvent(Dictionary<string, object> data)
        {
            if (data == null) throw new InvalidDataException("일정 응답이 비어 있습니다.");
            var start = JsonData.Object(data, "start"); var end = JsonData.Object(data, "end"); bool allDay = JsonData.String(start, "date") != null;
            return new RemoteEvent { Id = JsonData.String(data, "id"), Summary = JsonData.String(data, "summary") ?? "", ETag = JsonData.String(data, "etag"), ColorId = JsonData.String(data, "colorId") ?? "",
                Start = CalendarRules.ParseDate(JsonData.String(start, allDay ? "date" : "dateTime"), allDay), End = CalendarRules.ParseDate(JsonData.String(end, allDay ? "date" : "dateTime"), allDay), AllDay = allDay,
                ForbiddenMetadata = GooglePayloadPolicy.ResponseNeedsReview(data), Description = JsonData.String(data, "description") ?? "",
                HasMeetOrAttendees = data.ContainsKey("attendees") || data.ContainsKey("conferenceData") || data.ContainsKey("hangoutLink"),
                HasRecurrence = data.ContainsKey("recurrence"),
                PrivateProperties = (JsonData.Object(JsonData.Object(data, "extendedProperties"), "private") ?? new Dictionary<string, object>())
                    .ToDictionary(p => p.Key, p => p.Value == null ? "" : p.Value.ToString()) };
        }
    }
    public sealed class SharePointReader
    {
        private readonly IHttpTransport transport; private readonly OAuthService auth; private readonly OAuthClientSettings client; private readonly bool approved;
        public SharePointReader(IHttpTransport transport, OAuthService auth, OAuthClientSettings client, bool approved) { this.transport = transport; this.auth = auth; this.client = client; this.approved = approved; }
        public static string ContentPath(string sharingLink)
        {
            Uri uri;
            if (!Uri.TryCreate(sharingLink, UriKind.Absolute, out uri) || uri.Scheme != "https" || !(uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase)) || !String.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("HTTPS SharePoint 공유 링크를 입력하세요.");
            var sharingId = "u!" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sharingLink)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "https://graph.microsoft.com/v1.0/shares/" + sharingId + "/driveItem/content";
        }
        public async Task<byte[]> Download(string sharingLink, string driveId, string itemId, CancellationToken cancellation)
        {
            string path = !String.IsNullOrEmpty(driveId) && !String.IsNullOrEmpty(itemId) ? "https://graph.microsoft.com/v1.0/drives/" + Uri.EscapeDataString(driveId) + "/items/" + Uri.EscapeDataString(itemId) + "/content" : ContentPath(sharingLink);
            var token = await auth.AccessToken(client, "microsoft-source", approved, cancellation);
            var reply = await transport.Send(new HttpRequestData { Url = path, Bearer = token }, cancellation);
            if (reply.Status == 302 || reply.Status == 303)
            {
                Uri location; if (!Uri.TryCreate(reply.Location, UriKind.Absolute, out location) || location.Scheme != "https") throw new InvalidDataException("공식 다운로드 리디렉션 형식 오류");
                // Short-lived URL is used once, without authorization header, never persisted/logged.
                reply = await transport.Send(new HttpRequestData { Url = reply.Location }, cancellation);
            }
            if (reply.Status != 200) throw new InvalidOperationException("SharePoint 읽기 실패 (응답 " + reply.Status + "). Files.Read/조직 접근 정책을 확인하세요.");
            if (reply.Bytes == null || reply.Bytes.Length < 4 || reply.Bytes[0] != 'P' || reply.Bytes[1] != 'K') throw new InvalidDataException("Excel 원본 응답이 아닙니다.");
            return reply.Bytes;
        }
    }
}
