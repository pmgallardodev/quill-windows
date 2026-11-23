using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;
using WpfApplication = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace Quill.Windows.UI.Theme;

/// <summary>
/// Installs the Quill design system into <see cref="WpfApplication.Resources"/> once at startup
/// (the app has no App.xaml) and keeps it in sync with the Windows light/dark setting.
/// Merge order matters: WPF-UI theme colors, WPF-UI control styles, Quill tokens, Quill
/// semantic colors (swapped on theme change), Quill control styles.
/// </summary>
internal static class ThemeBootstrapper
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static WpfApplication? _application;
    private static ResourceDictionary? _lightColors;
    private static ResourceDictionary? _darkColors;
    private static ResourceDictionary? _activeColors;

    /// <summary>The backdrop new windows should use: Mica on Windows 11, none elsewhere
    /// (Windows 10 or high contrast), where the solid ApplicationBackgroundBrush is used.</summary>
    public static WindowBackdropType PreferredBackdrop =>
        !SystemParameters.HighContrast && WindowBackdrop.IsSupported(WindowBackdropType.Mica)
            ? WindowBackdropType.Mica
            : WindowBackdropType.None;

    /// <summary>Call once, after creating the Application and before <c>Run()</c>. Idempotent.</summary>
    public static void Initialize(WpfApplication application)
    {
        if (_application is not null)
        {
            return;
        }

        _application = application;
        _lightColors = Load("Colors.Light.xaml");
        _darkColors = Load("Colors.Dark.xaml");

        var theme = ResolveSystemTheme();
        _activeColors = ColorsFor(theme);

        var dictionaries = application.Resources.MergedDictionaries;
        dictionaries.Add(new ThemesDictionary { Theme = theme });
        dictionaries.Add(new ControlsDictionary());
        dictionaries.Add(Load("Tokens.xaml"));
        dictionaries.Add(_activeColors);
        dictionaries.Add(Load("Controls/Controls.xaml"));

        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        // A tray app may have no open window to receive WM_SETTINGCHANGE, so follow the
        // system setting through SystemEvents. Windows attached with Attach()
        // additionally get WPF-UI's own watcher for their backdrop.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        application.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        // Backdrop None here: existing plain windows keep their own background. Windows that want
        // Mica opt in through Attach().
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
    }

    /// <summary>
    /// Opts a window into Mica (with graceful fallback), live theme following and discreet mode
    /// (capture exclusion, see <see cref="Privacy.DiscreetMode"/>). Call from the
    /// window constructor. Best used with <c>ui:FluentWindow</c>; for a plain Window the backdrop
    /// only shows if its Background is transparent.
    /// </summary>
    public static void Attach(WpfWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var backdrop = PreferredBackdrop;
        if (window is FluentWindow fluent)
        {
            fluent.WindowBackdropType = backdrop;
        }

        SystemThemeWatcher.Watch(window, backdrop, updateAccents: true);

        // Every Quill window passes through here before its HWND exists, so discreet mode can
        // exclude it from screen capture before the first frame is drawn.
        Privacy.DiscreetMode.Track(window);
    }

    /// <summary>Reads the Windows "app mode" (Settings > Personalization > Colors).</summary>
    public static ApplicationTheme ResolveSystemTheme()
    {
        if (SystemParameters.HighContrast)
        {
            return ApplicationTheme.HighContrast;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return ApplicationTheme.Light;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility))
        {
            return;
        }

        // SystemEvents may raise on its own thread; resources belong to the UI thread.
        _application?.Dispatcher.BeginInvoke(() =>
        {
            var theme = ResolveSystemTheme();
            if (theme != ApplicationThemeManager.GetAppTheme())
            {
                ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
            }
            else
            {
                ApplicationAccentColorManager.ApplySystemAccent();
            }
        });
    }

    private static void OnApplicationThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        if (_application is null || _activeColors is null)
        {
            return;
        }

        var next = ColorsFor(theme);
        if (ReferenceEquals(next, _activeColors))
        {
            return;
        }

        var dictionaries = _application.Resources.MergedDictionaries;
        var index = dictionaries.IndexOf(_activeColors);
        if (index < 0)
        {
            return;
        }

        dictionaries[index] = next;
        _activeColors = next;
    }

    // High contrast picks the palette by the HC window background (HC White vs HC Black...);
    // WPF-UI swaps neutrals to the HC system colors on its own.
    private static ResourceDictionary ColorsFor(ApplicationTheme theme)
    {
        var useLight = theme switch
        {
            ApplicationTheme.Dark => false,
            ApplicationTheme.HighContrast => IsLight(System.Windows.SystemColors.WindowColor),
            _ => true,
        };
        return useLight ? _lightColors! : _darkColors!;
    }

    private static bool IsLight(System.Windows.Media.Color color) =>
        (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B) > 128;

    private static ResourceDictionary Load(string relativePath)
    {
        var assembly = typeof(ThemeBootstrapper).Assembly.GetName().Name;
        return new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{assembly};component/UI/Theme/{relativePath}", UriKind.Absolute),
        };
    }
}
