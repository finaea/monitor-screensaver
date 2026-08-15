// This project references WinForms (for the tray NotifyIcon) alongside WPF, so a
// dozen type names exist in both worlds. Pin every one of them to the WPF meaning
// project-wide; the few places that genuinely want the WinForms/GDI+ type qualify
// it explicitly (see App.xaml.cs and UI/DarkMenu.cs).

global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Button = System.Windows.Controls.Button;
global using RadioButton = System.Windows.Controls.RadioButton;
global using Cursors = System.Windows.Input.Cursors;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using KeyEventHandler = System.Windows.Input.KeyEventHandler;
global using Point = System.Windows.Point;
