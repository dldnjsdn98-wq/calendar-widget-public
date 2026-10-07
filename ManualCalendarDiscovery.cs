using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CalendarWidget
{
    public static class ManualCalendarDiscovery
    {
        public static async Task<List<ManualCalendarChoice>> FromExistingGrant(string root, WidgetConfiguration config, CancellationToken cancellation)
        {
            using (var transport = new HttpTransport()) {
                var workflow = new SourceWorkflow(root, transport);
                return await Discover(config, account => workflow.GoogleExisting(config, account).Calendars(cancellation));
            }
        }
        public static async Task<List<ManualCalendarChoice>> Discover(WidgetConfiguration config, Func<AccountConfiguration, Task<List<RemoteCalendar>>> readCalendars)
        {
            var choices = new List<ManualCalendarChoice>();
            var profiles = ConnectionProfiles.Effective(config);
            foreach (var account in config.Accounts.Where(a => !String.IsNullOrWhiteSpace(a.Id) && !String.IsNullOrWhiteSpace(a.GoogleSubject))) {
                var available = await readCalendars(account);
                foreach (var calendar in available.Where(CalendarAccess.CanWrite).GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())) {
                    var profile = profiles.SingleOrDefault(p => p.AccountId == account.Id && p.CalendarId == calendar.Id);
                    choices.Add(new ManualCalendarChoice { AccountId = account.Id, AccountSubject = account.GoogleSubject,
                        CalendarId = calendar.Id, Label = ConnectionProfiles.Name(calendar.Name, profile == null ? null : profile.DisplayName) +
                        " (" + (String.IsNullOrWhiteSpace(account.VerifiedEmail) ? account.GoogleSubject : account.VerifiedEmail) + ")" });
                }
            }
            return choices;
        }
    }
}
