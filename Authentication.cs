using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;

namespace CalendarWidget
{
    public static class RuntimePermissions { public static bool OAuthEnabled; }
    public sealed class HttpRequestData { public string Method = "GET", Url, Body, Bearer, ETag; public string ContentType = "application/json"; }
    public sealed class HttpReply { public int Status; public string Body; public byte[] Bytes; public string Location; }
    public interface IHttpTransport { Task<HttpReply> Send(HttpRequestData request, CancellationToken cancellation); }
    public sealed class HttpTransport : IHttpTransport, IDisposable
    {
        private readonly HttpClient client;
        public HttpTransport() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(45) }; }
        public async Task<HttpReply> Send(HttpRequestData data, CancellationToken cancellation)
        {
            try
            {
                using (var request = new HttpRequestMessage(new HttpMethod(data.Method), data.Url))
                {
                    if (data.Bearer != null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", data.Bearer);
                    if (data.ETag != null) request.Headers.TryAddWithoutValidation("If-Match", data.ETag);
                    if (data.Body != null) request.Content = new StringContent(data.Body, Encoding.UTF8, data.ContentType);
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation))
                    {
                        if (response.Content.Headers.ContentLength > 64 * 1024 * 1024) throw new InvalidDataException("응답 크기 제한 초과");
                        byte[] bytes;
                        using (var content = await response.Content.ReadAsStreamAsync()) using (var output = new MemoryStream())
                        {
                            var buffer = new byte[8192]; int count;
                            while ((count = await content.ReadAsync(buffer, 0, buffer.Length, cancellation)) > 0) { if (output.Length + count > 64 * 1024 * 1024) throw new InvalidDataException("응답 크기 제한 초과"); output.Write(buffer, 0, count); }
                            bytes = output.ToArray();
                        }
                        return new HttpReply { Status = (int)response.StatusCode, Bytes = bytes, Body = bytes.Length < 2000000 ? Encoding.UTF8.GetString(bytes) : null, Location = response.Headers.Location == null ? null : response.Headers.Location.ToString() };
                    }
                }
            }
            catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) throw; throw new CalendarApiException(0); }
            catch (HttpRequestException) { throw new CalendarApiException(0); }
        }
        public void Dispose() { client.Dispose(); }
    }
    public static class JsonData
    {
        public static Dictionary<string, object> Parse(string body) { if (body == null || body.Length > 2000000) throw new InvalidDataException("JSON 응답 제한 초과"); return new JavaScriptSerializer { MaxJsonLength = 2000000 }.DeserializeObject(body) as Dictionary<string, object>; }
        public static string String(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null; }
        public static Dictionary<string, object> Object(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) ? value as Dictionary<string, object> : null; }
        public static IEnumerable<object> Array(Dictionary<string, object> data, string key) { object value; return data != null && data.TryGetValue(key, out value) && value is object[] ? (object[])value : new object[0]; }
        public static string Serialize(object value) { return new JavaScriptSerializer().Serialize(value); }
    }
    public sealed class OAuthToken
    {
        public string AccessToken { get; set; } public string RefreshToken { get; set; } public DateTimeOffset ExpiresAt { get; set; }
        public string ClientId { get; set; } public string Subject { get; set; } public string VerifiedEmail { get; set; } public string Scopes { get; set; }
    }
    public sealed class ProtectedStore
    {
        private readonly string directory;
        public ProtectedStore(string directory) { this.directory = directory; }
        private string FilePath(string key) { return Path.Combine(directory, SyncHash.Text(key) + ".protected"); }
        public bool Exists(string key) { return File.Exists(FilePath(key)); }
        public void ValidateAvailability()
        {
            var synthetic = Encoding.UTF8.GetBytes("CalendarWidget encryption availability check");
            var protectedValue = ProtectedData.Protect(synthetic, null, DataProtectionScope.CurrentUser);
            if (!ProtectedData.Unprotect(protectedValue, null, DataProtectionScope.CurrentUser).SequenceEqual(synthetic)) throw new CryptographicException("사용자 암호화를 사용할 수 없습니다.");
        }
        public void Save(string key, string value)
        {
            Directory.CreateDirectory(directory);
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes("CalendarWidget|" + key), DataProtectionScope.CurrentUser);
            var path = FilePath(key); var temporary = path + ".tmp"; File.WriteAllBytes(temporary, encrypted);
            var sid = WindowsIdentity.GetCurrent().User;
            var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(sid); security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            File.SetAccessControl(temporary, security);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public string Load(string key)
        {
            var bytes = File.ReadAllBytes(FilePath(key));
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes("CalendarWidget|" + key), DataProtectionScope.CurrentUser));
        }
    }
    public sealed class OAuthExchangeException : InvalidOperationException
    {
        public readonly int HttpStatus; public readonly string ErrorCode, SafeDescription;
        public OAuthExchangeException(int status, string code, string description) : base("OAuth 토큰 교환 실패 (응답 " + status + ") · " + code + " · " + description) { HttpStatus = status; ErrorCode = code; SafeDescription = description; }
        public static OAuthExchangeException FromReply(HttpReply reply)
        {
            string code = "unknown_error", description = "제공자 설명은 안전하게 생략했습니다.";
            try {
                var data = JsonData.Parse(reply.Body); string candidate = JsonData.String(data, "error");
                if (System.Text.RegularExpressions.Regex.IsMatch(candidate ?? "", "^[a-z_]{1,64}$")) code = candidate;
                candidate = JsonData.String(data, "error_description");
                string[] safe = { "client_secret is missing.", "Missing required parameter: client_secret", "Client secret is missing.", "Invalid client secret", "Invalid client secret.", "Invalid client ID", "Invalid client ID.", "The OAuth client was not found.", "Bad Request", "Malformed auth code.", "Code was already redeemed.", "Token has been expired or revoked.", "Missing code verifier.", "Invalid code verifier.", "Missing required parameter: code_verifier", "Unauthorized" };
                if (safe.Contains(candidate)) description = candidate;
            } catch { }
            return new OAuthExchangeException(reply.Status, code, description);
        }
    }
    public sealed class OAuthClientSettings
    {
        public string Provider, ClientId, ClientSecret, AuthorizationEndpoint, TokenEndpoint;
        public string Scope;
        public static OAuthClientSettings Google(string file)
        {
            var data = JsonData.Parse(File.ReadAllText(file)); var installed = JsonData.Object(data, "installed");
            if (installed == null || String.IsNullOrWhiteSpace(JsonData.String(installed, "client_id"))) throw new InvalidDataException("Google 데스크톱용 installed 클라이언트 JSON이 필요합니다.");
            return new OAuthClientSettings { Provider = "google", ClientId = JsonData.String(installed, "client_id"), ClientSecret = JsonData.String(installed, "client_secret"),
                AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth", TokenEndpoint = "https://oauth2.googleapis.com/token",
                Scope = "openid email https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events" };
        }
        public static OAuthClientSettings Microsoft(string clientId, string tenant)
        {
            Guid id; if (!Guid.TryParse(clientId, out id) || !System.Text.RegularExpressions.Regex.IsMatch(tenant ?? "", @"^(organizations|common|[a-zA-Z0-9.-]+)$")) throw new InvalidDataException("Microsoft 공개 데스크톱 앱 Client ID와 테넌트를 설정하세요.");
            return new OAuthClientSettings { Provider = "microsoft", ClientId = clientId, AuthorizationEndpoint = "https://login.microsoftonline.com/" + tenant + "/oauth2/v2.0/authorize", TokenEndpoint = "https://login.microsoftonline.com/" + tenant + "/oauth2/v2.0/token", Scope = "offline_access https://graph.microsoft.com/Files.Read" };
        }
        public static OAuthClientSettings MicrosoftWrite(string clientId, string tenant)
        {
            var read = Microsoft(clientId, tenant);
            read.Scope = "offline_access https://graph.microsoft.com/Files.ReadWrite";
            return read;
        }
    }
    public sealed class PkceRequest
    {
        public string Verifier, Challenge, State, Url, Redirect;
        public static PkceRequest Create(OAuthClientSettings client, int port)
        {
            var bytes = new byte[32]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            string verifier = Base64Url(bytes); byte[] challenge; using (var sha = SHA256.Create()) challenge = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var request = new PkceRequest { Verifier = verifier, Challenge = Base64Url(challenge), State = Base64Url(bytes), Redirect = "http://" + (client.Provider == "microsoft" ? "localhost" : "127.0.0.1") + ":" + port + "/" };
            request.Url = client.AuthorizationEndpoint + "?" + Form(new Dictionary<string, string> { { "client_id", client.ClientId }, { "redirect_uri", request.Redirect }, { "response_type", "code" }, { "scope", client.Scope }, { "state", request.State }, { "code_challenge", request.Challenge }, { "code_challenge_method", "S256" }, { "access_type", "offline" }, { "prompt", client.Provider == "google" ? "consent select_account" : "select_account" } });
            return request;
        }
        private static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        public static string Form(Dictionary<string, string> values) { return String.Join("&", values.Where(p => p.Value != null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))); }
        public string ValidateCallback(string path)
        {
            var uri = new Uri(new Uri(Redirect), path); if (uri.AbsolutePath != "/") throw new InvalidOperationException("OAuth 콜백 경로 오류");
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query.GetValues("state") == null || query.GetValues("state").Length != 1 || query["state"] != State) throw new InvalidOperationException("OAuth state 검증 실패");
            if (query["error"] != null || String.IsNullOrWhiteSpace(query["code"])) throw new InvalidOperationException("사용자 인증이 완료되지 않았습니다.");
            return query["code"];
        }
    }
    public sealed class OAuthService
    {
        private readonly IHttpTransport transport; private readonly ProtectedStore store;
        public OAuthService(IHttpTransport transport, ProtectedStore store) { this.transport = transport; this.store = store; }
        public bool Connected(string key) { return store.Exists(key); }
        public async Task<OAuthToken> Connect(OAuthClientSettings client, string key, string expectedEmail, bool approved, CancellationToken cancellation)
        {
            if (!approved) throw new InvalidOperationException("새 OAuth 동의와 지속 저장 실행은 아직 승인되지 않았습니다.");
            store.ValidateAvailability();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var request = PkceRequest.Create(client, ((IPEndPoint)listener.LocalEndpoint).Port);
                Process.Start(new ProcessStartInfo(request.Url) { UseShellExecute = true });
                var accepting = listener.AcceptTcpClientAsync();
                if (await Task.WhenAny(accepting, Task.Delay(TimeSpan.FromMinutes(3), cancellation)) != accepting) throw new OperationCanceledException("인증 대기 시간이 지났습니다.");
                string code;
                using (var connection = await accepting)
                {
                    connection.ReceiveTimeout = 10000; connection.SendTimeout = 10000;
                    using (var stream = connection.GetStream())
                    {
                        byte[] buffer = new byte[8192]; int length = 0;
                        while (length < buffer.Length)
                        {
                            var reading = stream.ReadAsync(buffer, length, buffer.Length - length, cancellation);
                            if (await Task.WhenAny(reading, Task.Delay(10000, cancellation)) != reading) throw new OperationCanceledException("콜백 대기 시간 초과");
                            int count = await reading; if (count == 0) break; length += count;
                            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n")) break;
                        }
                        string first = Encoding.ASCII.GetString(buffer, 0, length).Split('\n')[0]; var parts = first.Split(' ');
                        if (parts.Length != 3 || parts[0] != "GET") throw new InvalidOperationException("OAuth 콜백 요청 오류");
                        code = request.ValidateCallback(parts[1]);
                        byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n<!doctype html><meta charset=utf-8><p>인증 응답을 받았습니다. 캘린더 위젯으로 돌아가세요.</p>");
                        await stream.WriteAsync(response, 0, response.Length, cancellation);
                    }
                }
                var fields = new Dictionary<string, string> { { "client_id", client.ClientId }, { "client_secret", client.ClientSecret }, { "grant_type", "authorization_code" }, { "code", code }, { "code_verifier", request.Verifier }, { "redirect_uri", request.Redirect } };
                var token = await Exchange(client, fields, cancellation);
                if (client.Provider == "google")
                {
                    var reply = await transport.Send(new HttpRequestData { Url = "https://openidconnect.googleapis.com/v1/userinfo", Bearer = token.AccessToken }, cancellation);
                    if (reply.Status != 200) throw new InvalidOperationException("실제 인증 계정 확인 실패");
                    var identity = JsonData.Parse(reply.Body); token.Subject = JsonData.String(identity, "sub"); token.VerifiedEmail = JsonData.String(identity, "email");
                    if (String.IsNullOrWhiteSpace(token.Subject) || !String.Equals(JsonData.String(identity, "email_verified"), "True", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("인증 계정의 확인된 이메일이 필요합니다.");
                    if (!String.IsNullOrWhiteSpace(expectedEmail) && !String.Equals(expectedEmail, token.VerifiedEmail, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("설정한 이메일과 실제 인증 계정이 다릅니다. 저장하지 않았습니다.");
                }
                if (String.IsNullOrEmpty(token.RefreshToken)) throw new InvalidOperationException("지속 접근용 refresh token이 없어 연결을 저장하지 않았습니다.");
                store.Save(key, JsonData.Serialize(token)); return token;
            }
            finally { listener.Stop(); }
        }
        private async Task<OAuthToken> Exchange(OAuthClientSettings client, Dictionary<string, string> fields, CancellationToken cancellation)
        {
            var reply = await transport.Send(new HttpRequestData { Method = "POST", Url = client.TokenEndpoint, ContentType = "application/x-www-form-urlencoded", Body = PkceRequest.Form(fields) }, cancellation);
            if (reply.Status != 200) throw OAuthExchangeException.FromReply(reply);
            var data = JsonData.Parse(reply.Body); int seconds; if (!Int32.TryParse(JsonData.String(data, "expires_in"), out seconds) || seconds <= 0 || String.IsNullOrEmpty(JsonData.String(data, "access_token"))) throw new InvalidOperationException("OAuth 응답 형식 오류");
            string scopes = JsonData.String(data, "scope") ?? client.Scope;
            var granted = scopes.Split(' ').Select(NormalizeScope).ToList(); if (client.Scope.Split(' ').Where(s => s != "offline_access").Select(NormalizeScope).Any(s => !granted.Contains(s))) throw new InvalidOperationException("필요한 읽기/일정 권한이 승인되지 않았습니다.");
            return new OAuthToken { ClientId = client.ClientId, AccessToken = JsonData.String(data, "access_token"), RefreshToken = JsonData.String(data, "refresh_token"), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds), Scopes = scopes };
        }
        private static string NormalizeScope(string scope) { return scope == "https://www.googleapis.com/auth/userinfo.email" ? "email" : scope.Replace("https://graph.microsoft.com/", ""); }
        public async Task<string> AccessToken(OAuthClientSettings client, string key, bool approved, CancellationToken cancellation, bool forceRefresh = false, bool allowRefresh = true)
        {
            if (!approved) throw new InvalidOperationException("지속 인증 실행은 아직 승인되지 않았습니다.");
            var old = new JavaScriptSerializer().Deserialize<OAuthToken>(store.Load(key));
            var granted = (old == null ? "" : old.Scopes ?? "").Split(' ').Select(NormalizeScope).ToList();
            if (client.Scope.Split(' ').Where(s => s != "offline_access").Select(NormalizeScope).Any(s => !granted.Contains(s)))
                throw new InvalidOperationException("Google 권한 확대를 위한 사용자 재동의가 필요합니다.");
            if (old == null || old.ClientId != client.ClientId) throw new InvalidOperationException("저장된 연결의 Client ID가 다릅니다.");
            if (!forceRefresh && old.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return old.AccessToken;
            if (!allowRefresh) throw new InvalidOperationException("기존 Google 연결 토큰이 만료됐습니다. 새 토큰 요청 없이 캘린더 저장을 보류합니다.");
            var token = await Exchange(client, new Dictionary<string, string> { { "client_id", client.ClientId }, { "client_secret", client.ClientSecret }, { "refresh_token", old.RefreshToken }, { "grant_type", "refresh_token" } }, cancellation);
            token.RefreshToken = token.RefreshToken ?? old.RefreshToken; token.Subject = old.Subject; token.VerifiedEmail = old.VerifiedEmail;
            store.Save(key, JsonData.Serialize(token)); return token.AccessToken;
        }
    }
}
