using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MicCastReceiver;

/// <summary>
/// 保证只有一个图形界面实例：第二个实例负责把已有窗口叫到前台然后退出。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\MicCastReceiver.SingleInstance";
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // 必须一直持有，否则被 GC 回收后互斥量就没了
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        // 用 Local\ 前缀：同一登录会话内唯一，不跨用户会话冲突
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    /// <summary>找到已经运行的窗口并激活它。</summary>
    public static void ActivateExisting()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            try
            {
                if (other.Id == current.Id) continue;
                IntPtr handle = other.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return;
            }
            catch
            {
                // 进程可能在枚举过程中退出，忽略
            }
            finally
            {
                other.Dispose();
            }
        }
    }
}
