using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CalendarWidget
{
    public static class StatusHotfixVerification
    {
        public static void RunUi(Widget widget, string output, Action<string, bool> check, Action<Window, string, double> capture)
        {
            var now = DateTimeOffset.Parse("2026-10-02T15:00:00+09:00");
            var remote = new RemoteEvent { Id = "synthetic-new-native", Summary = "Synthetic new event", Start = now.AddDays(2), End = now.AddDays(2).AddHours(1), ETag = "v1" };
            var book = new WorkbookEvents { DocumentId = "synthetic-document", Events = new List<SourceEvent>() };
            var repo = new SyncRepository(Path.Combine(output, "hotfix-projection-" + Guid.NewGuid().ToString("N") + ".json"));
            var snapshot = ConnectedCalendarSource.Project(book, repo, "synthetic-scope", "Synthetic calendar", "#9569D5",
                new List<RemoteEvent> { remote }, now, "synthetic-subject", "synthetic-calendar", "synthetic@example.invalid");
            var date = CalendarRules.InSeoul(remote.Start).Date;
            var view = CalendarRules.ProjectDate(snapshot, snapshot.Calendars[0].Id, date, now).Single();
            var popup = new DayAgendaPopup(widget.Window, widget.Root, date, new[] { view }, delegate(EventView selected) { });
            popup.ShowAt(new Point(160, 100)); popup.AgendaList.SelectedIndex = 0;
            check("hotfix_new_event_on_excel_profile_shows_status_edit_button", view.CanEdit && view.StatusContext.GoogleOnly &&
                popup.EditButton.Visibility == Visibility.Visible && popup.EditButton.IsEnabled);
            capture(popup.Window, Path.Combine(output, "hotfix-new-event-status-editor.png"), 96);
            popup.Window.Close();
        }
    }
}
