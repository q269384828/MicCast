using Microsoft.Win32;

namespace MicCastReceiver;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 只影响当前用户，不需要管理员权限。
/// </summary>
public static class Startup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MicCastReceiver";

    /// <summary>自启时带的参数：自动开始接收，并直接藏到托盘不弹窗。</summary>
    public const string AutoStartArgs = "--autostart --minimized";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前登记的启动命令，未启用时返回空串。</summary>
    public static string GetCommand()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static (bool Ok, string Message) Enable()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            string cmd = $"\"{exe}\" {AutoStartArgs}";
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key.SetValue(AppName, cmd, RegistryValueKind.String);
            return (true, cmd);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(AppName) is not null)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
