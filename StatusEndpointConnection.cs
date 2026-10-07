using System;
using System.IO;
using System.Linq;

namespace CalendarWidget
{
    public sealed class StatusEndpointConnection : IDisposable
    {
        public IStatusEndpoint Document { get; private set; }
        public IStatusEndpoint Google { get; private set; }
        private readonly HttpTransport transport;
        public StatusEndpointConnection(IStatusEndpoint document, IStatusEndpoint google, HttpTransport transport)
        { Document = document; Google = google; this.transport = transport; }
        public void Dispose() { if (transport != null) transport.Dispose(); }
        public static StatusEndpointConnection Create(string root, WidgetConfiguration configuration, StatusEditContext context)
        {
            if (context.GoogleOnly) {
                var matches = configuration.Accounts.Where(a => a.GoogleSubject == context.AccountId).ToList();
                if (matches.Count != 1 || String.IsNullOrEmpty(configuration.OAuthClientFile)) return new StatusEndpointConnection(null, null, null);
                var transportOnly = new HttpTransport();
                try {
                    var workflowOnly = new SourceWorkflow(root, transportOnly); var accountOnly = matches[0];
                    if (!workflowOnly.Credentials.Exists("google-" + accountOnly.Id)) { transportOnly.Dispose(); return new StatusEndpointConnection(null, null, null); }
                    var clientOnly = OAuthClientSettings.Google(configuration.OAuthClientFile);
                    var tokenOnly = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<OAuthToken>(workflowOnly.Credentials.Load("google-" + accountOnly.Id));
                    if (tokenOnly == null || tokenOnly.ClientId != clientOnly.ClientId || !CalendarAccess.HasEventWriteScope(tokenOnly.Scopes) ||
                        (tokenOnly.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2) && String.IsNullOrEmpty(tokenOnly.RefreshToken))) { transportOnly.Dispose(); return new StatusEndpointConnection(null, null, null); }
                    var googleOnly = new GoogleGateway(transportOnly, workflowOnly.Auth, clientOnly, "google-" + accountOnly.Id, true, true);
                    return new StatusEndpointConnection(null, new GoogleStatusEndpoint(googleOnly, context.AccountId, context.CalendarId, true), transportOnly);
                } catch { transportOnly.Dispose(); return new StatusEndpointConnection(null, null, null); }
            }
            bool documentMatches = configuration.SourceDocumentId == context.Binding.DocumentId ||
                (configuration.ExcelOptions != null && configuration.ExcelOptions.DocumentId == context.Binding.DocumentId);
            if (configuration.SourceKind != "local" || !documentMatches || configuration.SourceWorksheet != context.Binding.Worksheet)
                return new StatusEndpointConnection(null, null, null);
            var options = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ExcelReadOptions>(JsonData.Serialize(configuration.ExcelOptions));
            options.DocumentId = context.Binding.DocumentId; options.Worksheet = configuration.SourceWorksheet;
            var local = new LocalWorkbookStatusEndpoint(configuration.SourceWorkbookPath, options);
            var accounts = configuration.Accounts.Where(a => a.GoogleSubject == context.AccountId && a.TargetCalendarId == context.CalendarId).ToList();
            if (accounts.Count != 1 || String.IsNullOrEmpty(configuration.OAuthClientFile)) return new StatusEndpointConnection(local, null, null);
            var account = accounts[0];
            var transport = new HttpTransport();
            try {
                var workflow = new SourceWorkflow(root, transport);
                if (!workflow.Credentials.Exists("google-" + account.Id)) { transport.Dispose(); return new StatusEndpointConnection(local, null, null); }
                var client = OAuthClientSettings.Google(configuration.OAuthClientFile);
                var token = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<OAuthToken>(workflow.Credentials.Load("google-" + account.Id));
                if (token == null || token.ClientId != client.ClientId || !CalendarAccess.HasEventWriteScope(token.Scopes) ||
                    (token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2) && String.IsNullOrEmpty(token.RefreshToken))) { transport.Dispose(); return new StatusEndpointConnection(local, null, null); }
                // An explicit save may renew the existing grant's short-lived access token.
                var google = new GoogleGateway(transport, workflow.Auth, client, "google-" + account.Id, true, true);
                return new StatusEndpointConnection(local, new GoogleStatusEndpoint(google, context.AccountId, context.CalendarId, true), transport);
            } catch { transport.Dispose(); return new StatusEndpointConnection(local, null, null); }
        }
    }
}
