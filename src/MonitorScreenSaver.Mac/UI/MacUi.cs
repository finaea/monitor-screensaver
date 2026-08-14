using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac.UI;

/// <summary>
/// Bridge between the AppKit-owned app shell and the Avalonia settings window.
///
/// Avalonia is set up with SetupWithoutStarting() — deliberately *not* with a desktop
/// lifetime. The lifetime is AppKit's ([NSApp run] in MacApp.Run), and Avalonia's
/// dispatcher rides the same main CFRunLoop, so one loop pumps the engine timer, the
/// status item and the settings window alike. Doing it the other way round (letting
/// Avalonia own the loop) would put a UI framework on the blanking path for the many
/// sessions where nobody ever opens settings.
/// </summary>
internal static class MacUi
{
    private static bool _initialised;
    private static SettingsWindow? _window;

    internal static void ShowSettings(MacApp app)
    {
        try
        {
            EnsureAvalonia();

            if (_window is null)
            {
                _window = new SettingsWindow(app);
                _window.Closed += (_, _) => _window = null;
                _window.Show();
            }
            else
            {
                _window.Show();
            }

            _window.Activate();

            // An accessory (LSUIElement) app is not in the activation rotation, so its
            // windows open behind whatever is frontmost unless we ask explicitly.
            var nsApp = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
            ObjC.SendVoid(nsApp, ObjC.Sel("activateIgnoringOtherApps:"), true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacUi.ShowSettings", ex);
        }
    }

    internal static void EnsureAvalonia()
    {
        if (_initialised) return;
        _initialised = true;

        AppBuilder.Configure<SettingsApp>()
            .UseAvaloniaNative()
            .UseSkia()
            .SetupWithoutStarting();
    }

    /// <summary>What <see cref="BuildThemeProbe"/> measured. See its remarks.</summary>
    internal sealed record ThemeProbe(
        bool Realised,
        double SliderWidth,
        double ToggleWidth,
        double CardWidth,
        double ContentWidth,
        bool WrapsInsideParent,
        IReadOnlyList<string> MissingBrushes);

    /// <summary>
    /// Realises one of every themed control off-screen and measures the result, for the
    /// selftest. Kept here rather than in MacSelfTest so the Avalonia API surface stays
    /// inside the UI folder.
    ///
    /// Two classes of bug are only visible once a control is actually templated and laid
    /// out, and both shipped during Phase 5: a Fluent theme resource overridden with the
    /// wrong CLR type (which throws when the control that reads it is first realised),
    /// and content that measures wider than the card it lives in (which pushed a card
    /// past the window edge). Measuring is enough — the window is never activated.
    /// </summary>
    internal static ThemeProbe BuildThemeProbe()
    {
        EnsureAvalonia();

        var missing = new List<string>();
        foreach (var key in new[] { "Bg", "Surface", "SurfaceAlt", "SurfaceHot", "Border", "Text",
                                    "TextMuted", "TextFaint", "Accent", "AccentSoft", "Ok", "Warn", "Danger" })
        {
            if (!(Application.Current?.TryFindResource(key, out var value) ?? false) || value is not IBrush)
                missing.Add(key);
        }

        var slider = new Slider { Minimum = 5, Maximum = 100, Value = 65, Width = 300 };

        // A long unbroken description is exactly what overflowed a card in Phase 5.
        var label = new TextBlock
        {
            Classes = { "mono" },
            Text = "Registered with launchd through SMAppService; keeps running in the menu bar " +
                   "across sleep and restarts, which is a deliberately long line.",
        };

        var toggle = new CheckBox
        {
            Classes = { "toggle" },
            IsChecked = true,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { "body" }, Text = "Start at login" },
                    label,
                },
            },
        };

        var card = new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { "h2" }, Text = "PROBE" },
                    toggle,
                    slider,
                    new TextBox { Classes = { "field" }, Text = "/tmp/probe.mp4", Width = 200 },
                    new Button { Classes = { "ghost" }, Content = "Browse…" },
                    new Button { Classes = { "primary" }, Content = "Blank now" },
                    new RadioButton { Classes = { "segment" }, Content = "True black", IsChecked = true },
                },
            },
        };

        // Off-screen and non-activating: it must never appear on a display the user is
        // looking at, and must never take focus from whatever is frontmost.
        var window = new Window
        {
            Width = 680,
            Height = 820,
            Position = new PixelPoint(-30000, -30000),
            ShowActivated = false,
            SystemDecorations = SystemDecorations.None,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel { Margin = new Thickness(18, 16, 14, 8), Children = { card } },
            },
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var content = (StackPanel)toggle.Content!;

            return new ThemeProbe(
                Realised: slider.Bounds.Width > 0 && toggle.Bounds.Width > 0 && card.Bounds.Width > 0,
                SliderWidth: slider.Bounds.Width,
                ToggleWidth: toggle.Bounds.Width,
                CardWidth: card.Bounds.Width,
                ContentWidth: content.Bounds.Width,
                // The card must not have been forced wider than the space it was given.
                WrapsInsideParent: card.DesiredSize.Width <= card.Bounds.Width + 0.5,
                MissingBrushes: missing);
        }
        finally
        {
            try { window.Close(); } catch { /* teardown */ }
        }
    }
}
