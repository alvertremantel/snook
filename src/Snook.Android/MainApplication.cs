using Android.Runtime;
using Avalonia.Android;

namespace Snook.Android;

[global::Android.App.Application]
public sealed class MainApplication(nint handle, JniHandleOwnership ownership)
    : AvaloniaAndroidApplication<SpikeApp>(handle, ownership)
{
    public override void OnCreate()
    {
        base.OnCreate();
        AndroidNotificationChannels.Ensure();
    }
}
