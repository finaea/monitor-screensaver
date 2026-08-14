using Avalonia;
using Avalonia.Markup.Xaml;

namespace MonitorScreenSaver.Mac.UI;

/// <summary>
/// The Avalonia application object for the settings window. It owns nothing but the
/// styles: the app's lifetime belongs to AppKit (see MacApp.Run), not to Avalonia, so
/// there is no main window and no desktop lifetime here.
/// </summary>
public sealed class SettingsApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
