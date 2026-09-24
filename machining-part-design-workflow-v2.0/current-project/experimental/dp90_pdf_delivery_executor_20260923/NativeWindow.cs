using System.Runtime.InteropServices;
static class NativeWindow {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hWnd,out RECT r);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hWnd,nint i,int x,int y,int cx,int cy,uint f);
 [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
 public static void GetVirtualScreen(out int x,out int y,out int w,out int h){x=GetSystemMetrics(76);y=GetSystemMetrics(77);w=GetSystemMetrics(78);h=GetSystemMetrics(79);}
}
