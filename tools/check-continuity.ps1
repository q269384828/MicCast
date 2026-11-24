# 录制 CABLE Output 若干秒，逐 5ms 帧扫描全零采样，报告最长断流。
# 用途：验证 MicCast 送进声卡的 PCM 是否连续（Win+H / 会议软件对空洞很敏感）。
#
#   pwsh tools/check-continuity.ps1 -Seconds 5
#
# 需要先让 MicCastReceiver 正常串流，否则录到的是纯静音。

param(
    [int]$Seconds = 5,
    [string]$DeviceName = 'CABLE Output',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

# Windows 自带的 waveIn 足够：16kHz / 16bit / mono，和 MicCast 下发的一致
$wavPath = if ($OutFile) { $OutFile } else { Join-Path $env:TEMP 'miccast-continuity.wav' }

Add-Type -ReferencedAssemblies System.Windows.Forms @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class Rec
{
    const int WAVE_MAPPER = -1;
    const int WAVE_FORMAT_PCM = 1;
    const int CALLBACK_NULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int dev, ref WAVEFORMATEX f, IntPtr cb, IntPtr i, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInGetNumDevs();

    public static byte[] Record(int seconds, int rate)
    {
        if (waveInGetNumDevs() == 0) throw new Exception("没有可用的录音设备");

        int bufferMs = 100;
        int blockAlign = 2;
        int bufSize = rate * blockAlign * bufferMs / 1000;
        int bufferCount = seconds * 1000 / bufferMs;

        var fmt = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = 1,
            nSamplesPerSec = (uint)rate,
            nAvgBytesPerSec = (uint)(rate * blockAlign),
            nBlockAlign = (ushort)blockAlign,
            wBitsPerSample = 16,
            cbSize = 0
        };

        IntPtr hWave;
        int rc = waveInOpen(out hWave, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (rc != 0) throw new Exception("waveInOpen 失败: " + rc);

        int hdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        var headers = new IntPtr[bufferCount];
        var datas = new IntPtr[bufferCount];
        try
        {
            for (int i = 0; i < bufferCount; i++)
            {
                datas[i] = Marshal.AllocHGlobal(bufSize);
                var hdr = new WAVEHDR { lpData = datas[i], dwBufferLength = (uint)bufSize };
                headers[i] = Marshal.AllocHGlobal(hdrSize);
                Marshal.StructureToPtr(hdr, headers[i], false);
                waveInPrepareHeader(hWave, headers[i], hdrSize);
                waveInAddBuffer(hWave, headers[i], hdrSize);
            }

            waveInStart(hWave);
            Thread.Sleep(seconds * 1000 + 300);
            waveInStop(hWave);
            waveInReset(hWave);

            var all = new byte[bufferCount * bufSize];
            int copied = 0;
            for (int i = 0; i < bufferCount; i++)
            {
                var hdr = (WAVEHDR)Marshal.PtrToStructure(headers[i], typeof(WAVEHDR));
                int n = (int)hdr.dwBytesRecorded;
                if (n <= 0) continue;
                Marshal.Copy(datas[i], all, copied, n);
                copied += n;
            }
            var result = new byte[copied];
            Array.Copy(all, result, copied);
            return result;
        }
        finally
        {
            for (int i = 0; i < bufferCount; i++)
            {
                if (headers[i] != IntPtr.Zero) { waveInUnprepareHeader(hWave, headers[i], hdrSize); Marshal.FreeHGlobal(headers[i]); }
                if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
            }
            waveInClose(hWave);
        }
    }

    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
}
'@

Write-Host "录音 $Seconds 秒（16kHz / 16bit / mono）..." -ForegroundColor Cyan
$pcm = [Rec]::Record($Seconds, 16000)
if ($pcm.Length -eq 0) { Write-Error '没录到数据，检查默认录音设备是否指向 CABLE Output'; exit 1 }

# 逐 5ms 帧（160 字节）判断是否为全零
$frameBytes = 160
$frames = [math]::Floor($pcm.Length / $frameBytes)
$silent = 0
$run = 0
$longestRun = 0
$gapCount = 0

for ($i = 0; $i -lt $frames; $i++) {
    $off = $i * $frameBytes
    $nonZero = $false
    for ($j = 0; $j -lt $frameBytes; $j += 2) {
        $sample = [BitConverter]::ToInt16($pcm, $off + $j)
        if ($sample -ne 0) { $nonZero = $true; break }
    }
    if ($nonZero) {
        if ($run -ge 40) { $gapCount++ }   # >=200ms 算一次明显断流
        if ($run -gt $longestRun) { $longestRun = $run }
        $run = 0
    } else {
        $silent++
        $run++
    }
}
if ($run -gt $longestRun) { $longestRun = $run }

$durationMs = [math]::Round($frames * 5, 0)
$silentPct = if ($frames -gt 0) { [math]::Round($silent * 100.0 / $frames, 1) } else { 0 }

Write-Host ''
Write-Host '===== 结果 =====' -ForegroundColor Green
Write-Host ("录到时长      : {0} ms" -f $durationMs)
Write-Host ("总帧数(5ms)   : {0}" -f $frames)
Write-Host ("全零帧(静音)  : {0} ({1}%)" -f $silent, $silentPct)
Write-Host ("最长连续静音  : {0} ms" -f ($longestRun * 5))
Write-Host ("≥200ms 断流   : {0} 次" -f $gapCount)
Write-Host ''
Write-Host '怎么读这些数字：' -ForegroundColor DarkGray
Write-Host '  纯静音环境下「全零帧」必然很高，那是环境安静，不代表断流。' -ForegroundColor DarkGray
Write-Host '  真正要看的是「最长连续静音」和断流次数 —— 对着麦克风持续说话时，' -ForegroundColor DarkGray
Write-Host '  这两个数应该很小；如果说话期间仍有几百毫秒空洞，说明推流在掉。' -ForegroundColor DarkGray
