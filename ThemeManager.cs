using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace CalendarWidget
{
    public static class ThemeManager
    {
        public static event Action AppearanceChanged;
        static ThemeManager()
        {
            SystemEvents.UserPreferenceChanged += delegate { RefreshSystemTheme(); };
            SystemParameters.StaticPropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs args) {
                if (String.IsNullOrEmpty(args.PropertyName) || args.PropertyName == "HighContrast") RefreshSystemTheme();
            };
        }
        private static void RefreshSystemTheme()
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(delegate {
                foreach (Window window in app.Windows) {
                    var settings = window.Resources["ThemeConfiguration"] as WidgetConfiguration;
                    if (settings != null) Apply(window, settings);
                }
                if (AppearanceChanged != null) AppearanceChanged();
            }));
        }
        public const string DefaultFont = "Malgun Gothic";
        public const double DefaultSize = 13;
        public static readonly string[] Modes = { "light", "dark", "system" };

        public static void Normalize(WidgetConfiguration settings)
        {
            if (!Modes.Contains(settings.ThemeMode)) settings.ThemeMode = "light";
            if (settings.DisplayFontSize < 11 || settings.DisplayFontSize > 18 || Double.IsNaN(settings.DisplayFontSize)) settings.DisplayFontSize = DefaultSize;
            if (String.IsNullOrWhiteSpace(settings.FontFamilyName) || !InstalledFonts().Contains(settings.FontFamilyName, StringComparer.OrdinalIgnoreCase)) settings.FontFamilyName = DefaultFont;
        }
        public static List<string> InstalledFonts()
        {
            return Fonts.SystemFontFamilies.Select(f => f.Source).Where(f => !String.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        public static bool IsDark(WidgetConfiguration settings)
        {
            if (settings.ThemeMode == "dark") return true;
            if (settings.ThemeMode != "system") return false;
            try {
                object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return Convert.ToInt32(value) == 0;
            } catch { return false; }
        }
        public static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        public static void Apply(Window window, WidgetConfiguration settings)
        {
            Normalize(settings);
            window.Resources["ThemeConfiguration"] = settings;
            bool dark = IsDark(settings), contrast = SystemParameters.HighContrast;
            var values = new Dictionary<string, string> {
                { "ThemeBackground", contrast ? SystemColors.WindowColor.ToString() : dark ? "#17161C" : "#F7F5FA" },
                { "ThemeSurface", contrast ? SystemColors.WindowColor.ToString() : dark ? "#24222B" : "#FFFFFF" },
                { "ThemeSurfaceAlt", contrast ? SystemColors.ControlColor.ToString() : dark ? "#302D38" : "#F7F5FA" },
                { "ThemeSurfaceTint", contrast ? SystemColors.ControlColor.ToString() : dark ? "#393143" : "#F1EAFB" },
                { "ThemeText", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#F6F2FA" : "#262230" },
                { "ThemeMuted", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#CEC6D6" : "#686272" },
                { "ThemeFaint", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#AEA5B8" : "#AAA4B2" },
                { "ThemeBorder", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#62596C" : "#E8E3EF" },
                { "ThemeInputBorder", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#82758E" : "#C9BEDC" },
                { "ThemeAccent", contrast ? SystemColors.HighlightColor.ToString() : "#6D3CCB" },
                { "ThemeAccentOutline", contrast ? SystemColors.HighlightColor.ToString() : dark ? "#C09BFF" : "#6D3CCB" },
                { "ThemeAccentText", contrast ? SystemColors.HighlightTextColor.ToString() : "#FFFFFF" },
                { "ThemeSecondary", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#BDA9E8" : "#7B4BBA" },
                { "ThemeError", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#FFA9A9" : "#AD3030" },
                { "ThemeInfo", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#C7D2EC" : "#596780" },
                { "ThemeWarning", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#FFCA91" : "#8F5520" },
                { "ThemeSunday", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#EAB0C5" : "#A34C6B" },
                { "ThemeSaturday", contrast ? SystemColors.WindowTextColor.ToString() : dark ? "#ABC5FF" : "#4F69A8" }
            };
            foreach (var pair in values) window.Resources[pair.Key] = Brush(pair.Value);
            window.Resources[SystemColors.WindowBrushKey] = (Brush)window.Resources["ThemeSurface"];
            window.Resources[SystemColors.WindowTextBrushKey] = (Brush)window.Resources["ThemeText"];
            window.Resources[SystemColors.ControlBrushKey] = (Brush)window.Resources["ThemeSurfaceAlt"];
            window.Resources[SystemColors.ControlTextBrushKey] = (Brush)window.Resources["ThemeText"];
            window.Resources[SystemColors.HighlightBrushKey] = (Brush)window.Resources["ThemeAccent"];
            window.Resources[SystemColors.HighlightTextBrushKey] = (Brush)window.Resources["ThemeAccentText"];
            double size = settings.DisplayFontSize;
            window.Resources["ThemeFontSize"] = size;
            window.Resources["ThemeTitleSize"] = size + 7;
            window.Resources["ThemeHeadingSize"] = size + 5;
            window.Resources["ThemeSmallSize"] = Math.Max(11, size - 2);
            window.Resources["ThemeTinySize"] = Math.Max(10, size - 3);
            window.Resources["ThemeDayHeight"] = Math.Max(44, size * 3.3);
            window.FontFamily = new FontFamily(settings.FontFamilyName);
            window.FontSize = size;
            window.Background = (Brush)window.Resources["ThemeBackground"];
            window.Foreground = (Brush)window.Resources["ThemeText"];
            window.InvalidateVisual();
        }
        public static void ApplyFromOwner(Window window, Window owner)
        {
            Apply(window, owner == null ? new WidgetConfiguration() : owner.Resources["ThemeConfiguration"] as WidgetConfiguration ?? new WidgetConfiguration());
        }
        public static Brush Get(Window window, string key) { return (Brush)window.Resources[key]; }
    }
}
