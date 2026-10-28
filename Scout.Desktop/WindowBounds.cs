using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace Scout.Desktop;
public partial class MainWindow {
 protected override void OnSourceInitialized(EventArgs e) {
  base.OnSourceInitialized(e);
  HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WorkAreaHook);
 }
 private static IntPtr WorkAreaHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
  if(message!=0x0024)return IntPtr.Zero; // WM_GETMINMAXINFO
  var monitor=MonitorFromWindow(hwnd,2);
  var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
  if(monitor==IntPtr.Zero||!GetMonitorInfo(monitor,ref info))return IntPtr.Zero;
  var limits=Marshal.PtrToStructure<MinMaxInfo>(lParam);
  // Win32 coordinates are physical pixels, relative to this monitor.
  // Use its work area, which excludes the taskbar on any edge.
  limits.MaxPosition=new PointI {X=info.Work.Left-info.Monitor.Left,Y=info.Work.Top-info.Monitor.Top};
  limits.MaxSize=new PointI {X=info.Work.Right-info.Work.Left,Y=info.Work.Bottom-info.Work.Top};
  limits.MinTrackSize.X=Math.Min(limits.MinTrackSize.X,limits.MaxSize.X);
  limits.MinTrackSize.Y=Math.Min(limits.MinTrackSize.Y,limits.MaxSize.Y);
  Marshal.StructureToPtr(limits,lParam,false);
  handled=true;
  return IntPtr.Zero;
 }
 [StructLayout(LayoutKind.Sequential)] private struct PointI {public int X,Y;}
 [StructLayout(LayoutKind.Sequential)] private struct RectI {public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo {public PointI Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
 [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public RectI Monitor,Work;public uint Flags;}
 [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
 [DllImport("user32.dll",CharSet=CharSet.Auto)] [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}
