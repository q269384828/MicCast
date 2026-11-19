using System.Diagnostics;
using System.IO.Compression;

namespace MicCastReceiver;

/// <summary>VB-CABLE 虚拟声卡的检测与一键安装。</summary>
public static class VbCable
{
    public const string HomePage = "https://vb-audio.com/Cable/";
    public const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";

    /// <summary>渲染设备里出现 "CABLE Input" 就认为装好了。</summary>
    public static bool IsInstalled()
        => AudioPlayer.GetRenderDevices().Any(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));

    public static void OpenHomePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(HomePage) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>
    /// 下载并静默安装：解压到临时目录后以管理员身份跑 VBCABLE_Setup_x64.exe -i -h。
    /// 会弹 UAC；装完不需要重启（实测端点立即出现）。
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(Action<string> log, CancellationToken token)
    {
        string work = Path.Combine(Path.GetTempPath(), "MicCast-VBCable");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            string zip = Path.Combine(work, "VBCABLE_Driver_Pack.zip");
            log("正在下载 VB-CABLE 驱动包…");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = File.Create(zip);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;
                    if (total > 0)
                    {
                        log($"正在下载 VB-CABLE 驱动包… {read * 100 / total.Value}%");
                    }
                }
            }
            log($"下载完成 ({new FileInfo(zip).Length / 1024} KB)，正在解压…");

            string extracted = Path.Combine(work, "pack");
            ZipFile.ExtractToDirectory(zip, extracted);

            string setup = Path.Combine(extracted, "VBCABLE_Setup_x64.exe");
            if (!File.Exists(setup))
            {
                return (false, "驱动包里没找到 VBCABLE_Setup_x64.exe");
            }

            log("正在安装驱动（会弹出 UAC，请点「是」）…");
            var info = new ProcessStartInfo(setup)
            {
                Arguments = "-i -h",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = extracted
            };
            using var process = Process.Start(info);
            if (process is null)
            {
                return (false, "无法启动安装程序");
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
            {
                return (false, $"安装程序退出码 {process.ExitCode}");
            }

            // 端点注册需要一两秒
            for (int i = 0; i < 20; i++)
            {
                if (IsInstalled()) break;
                await Task.Delay(500, token);
            }

            return IsInstalled()
                ? (true, "VB-CABLE 安装完成")
                : (false, "安装程序已结束，但没有检测到 CABLE Input，可能需要重启后再试");
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消");
        }
        catch (Exception ex)
        {
            return (false, $"安装失败: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }
}
