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
    public override void Initialize()
    {
        // Avalonia builds the macOS app menu from this, and its default is the literal
        // string "Avalonia Application" — which is what the menu bar and its Hide item said
        // while the settings window was open. CFBundleName does not override it: the menu
        // is Avalonia's own, not the one AppKit derives from the plist. (The About item is
        // hardcoded "About Avalonia" and does not follow this; replacing it means supplying
        // a whole NativeMenu, which is not worth it for one item one level down.)
        Name = "MonitorScreenSaver";

        AvaloniaXamlLoader.Load(this);
    }
}
