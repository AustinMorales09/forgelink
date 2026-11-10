using Android.App;
using Android.Content.PM;

namespace ForgeLink.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // Rugged handheld scanners (Zebra DataWedge, Honeywell) can be configured for keystroke output,
    // which lands in the focused ScanInput like a keyboard-wedge scanner on the desktop.
}
