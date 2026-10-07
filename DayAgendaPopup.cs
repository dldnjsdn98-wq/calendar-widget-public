using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace CalendarWidget
{
    public sealed class DayAgendaPopup
    {
        public readonly Window Window;
        public readonly DateTime Date;
        public string CloseReason { get; private set; }
        public readonly ListBox AgendaList;
        public readonly Button EditButton;
        private readonly Action<EventView> edit;
        private bool outsideClickArmed;
        public EventView SelectedEvent { get { return AgendaList.SelectedItem as EventView; } }
        public DayAgendaPopup(Window owner, string root, DateTime date, IList<EventView> events, Action<EventView> edit, Action<DateTime> add = null)
        {
            Date = date.Date; this.edit = edit;
            using (var input = File.OpenRead(Path.Combine(root, "day-popup.xaml"))) Window = (Window)XamlReader.Load(input);
            Window.Owner = owner;
            ThemeManager.ApplyFromOwner(Window, owner);
            Find<TextBlock>("PopupDate").Text = Date.ToString("yyyy년 M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR"));
            Find<TextBlock>("PopupCount").Text = events.Count + "개 일정";
            AgendaList = Find<ListBox>("AgendaList"); AgendaList.ItemsSource = events;
            EditButton = Find<Button>("PopupEditStatusButton");
            Find<Button>("PopupAddEventButton").Click += delegate { CloseReason = "add"; Window.Close(); if (add != null) add(Date); };
            Find<TextBlock>("EmptyDayText").Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AgendaList.Visibility = events.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AgendaList.SelectionChanged += delegate { ShowDetail(SelectedEvent); };
            EditButton.Click += delegate { var selected = SelectedEvent; if (selected == null || !selected.CanEdit) return; CloseReason = "edit"; Window.Close(); this.edit(selected); };
            Find<Button>("PopupCloseButton").Click += delegate { CloseReason = "button"; Window.Close(); };
            Window.PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { args.Handled = true; CloseReason = "escape"; Window.Close(); } };
            Window.Deactivated += delegate { if (outsideClickArmed && Window.IsVisible) { CloseReason = "outside"; Window.Close(); } };
            EventHandler moved = delegate { if (Window.IsVisible) { CloseReason = "owner-moved"; Window.Close(); } };
            SizeChangedEventHandler resized = delegate { if (Window.IsVisible) { CloseReason = "owner-resized"; Window.Close(); } };
            owner.LocationChanged += moved; owner.SizeChanged += resized;
            Window.Closed += delegate { owner.LocationChanged -= moved; owner.SizeChanged -= resized; };
            Find<Grid>("PopupHeader").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) { if (args.OriginalSource is Grid || args.OriginalSource is TextBlock) try { Window.DragMove(); } catch (InvalidOperationException) { } };
        }
        public T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        private void ShowDetail(EventView view)
        {
            Find<Border>("EventDetail").Visibility = view == null ? Visibility.Collapsed : Visibility.Visible;
            if (view == null) return;
            Find<TextBlock>("DetailTitle").Text = view.Title;
            Find<TextBlock>("DetailSchedule").Text = view.Schedule;
            Find<ItemsControl>("DetailFields").ItemsSource = view.Fields;
            Find<TextBlock>("DetailPending").Text = view.PendingStatus ?? "";
            EditButton.IsEnabled = view.CanEdit; EditButton.ToolTip = view.EditHint;
            EditButton.Content = view.StatusContext != null && view.StatusContext.GoogleOnly && !view.StatusContext.HasRemoteStatus ? "상태 추가" : "상태 편집";
            EditButton.Visibility = view.CanEdit ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("NativeCalendarNotice").Text = view.Origin == "excel-unlinked" ? "원본 셀 연결이 없어 Google 등록과 상태 편집을 보류한 일정입니다." : "Google에서 읽은 일정입니다. 원본 상태 연결이 없습니다.";
            Find<TextBlock>("NativeCalendarNotice").Visibility = view.Origin == "google-direct" || view.Origin == "excel-unlinked" ? Visibility.Visible : Visibility.Collapsed;
            if (view.Origin == "google-direct") Find<TextBlock>("NativeCalendarNotice").Text = view.CanEdit ?
                "이 일정은 Google에서 읽었습니다. 상태 추가·편집은 Google 일정 설명에 저장됩니다." :
                "이 일정은 Google에서 읽었습니다. 이 계정에는 상태 편집 권한이 없습니다.";
        }
        public static Point AnchorPoint(FrameworkElement anchor)
        {
            var point = anchor.PointToScreen(new Point(anchor.ActualWidth, 0));
            var source = PresentationSource.FromVisual(anchor);
            return source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(point) : point;
        }
        public void ShowAt(Point point)
        {
            var work = SystemParameters.WorkArea;
            Window.Width = Math.Min(Window.Width, Math.Max(Window.MinWidth, work.Width - 16));
            Window.Height = Math.Min(Window.Height, Math.Max(Window.MinHeight, work.Height - 16));
            Window.Left = Math.Max(work.Left + 8, Math.Min(point.X, work.Right - Window.Width - 8));
            Window.Top = Math.Max(work.Top + 8, Math.Min(point.Y, work.Bottom - Window.Height - 8));
            Window.Show();
            Window.Dispatcher.BeginInvoke(new Action(delegate { if (Window.IsVisible) { Window.Activate(); outsideClickArmed = true; } }), DispatcherPriority.ApplicationIdle);
        }
    }
}
