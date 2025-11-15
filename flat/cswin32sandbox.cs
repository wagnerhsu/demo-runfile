#:property TargetFramework=net10.0-windows
#:package Microsoft.Windows.CsWin32@0.3.242
#:package Humanizer@3.0.0-rc.30

using Windows.Win32;
using Humanizer;

var ticks = PInvoke.GetTickCount64();
Console.WriteLine($"System has been running for {TimeSpan.FromMilliseconds((long)ticks).Humanize(precision: 4)}.");
