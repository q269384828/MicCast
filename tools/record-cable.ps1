# 从指定录音设备录制一段 16kHz/16bit/mono PCM，存成标准 WAV。
# 用于对比 MicCast 三档采集模式的实际听感。
#
#   pwsh tools/record-cable.ps1 -Seconds 8 -OutFile C:\temp\raw.wav
#   pwsh tools/record-cable.ps1 -DeviceIndex 0 -Seconds 8 -OutFile out.wav
#
# 不指定 -DeviceIndex 时自动按名字找包含 "CABLE Output" 的设备。
# 这样不必改系统默认录音设备，不会打扰使用者。

param(
    [int]$Seconds = 8,
    [int]$DeviceIndex = -1,
    [string]$DeviceName = 'CABLE Output',
    [string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

if (-not $OutFile) {
    $OutFile = Join-Path $env:TEMP ("miccast-{0:HHmmss}.wav" -f (Get-Date))
}

Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class WavRec
{
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CAPS
    {
        public ushort wMid, wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels, wReserved1;
    }

    [DllImport("winmm.dll")] static extern int waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int waveInGetDevCaps(int id, ref CAPS caps, int size);
    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int dev, ref WAVEFORMATEX f, IntPtr cb, IntPtr i, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

    public static string[] ListDevices()
    {
        int n = waveInGetNumDevs();
        var list = new string[n];
        for (int i = 0; i < n; i++)
        {
            var c = new CAPS();
            waveInGetDevCaps(i, ref c, Marshal.SizeOf(typeof(CAPS)));
            list[i] = c.szPname ?? string.Empty;
        }
        return list;
    }

    public static byte[] Record(int deviceIndex, int seconds, int rate)
    {
        int blockAlign = 2;
        int bufMs = 100;
        int bufSize = rate * blockAlign * bufMs / 1000;
        int bufferCount = seconds * 1000 / bufMs;

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
        int rc = waveInOpen(out hWave, deviceIndex, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (rc != 0) throw new Exception("waveInOpen 失败，返回码 " + rc);

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
            Thread.Sleep(seconds * 1000 + 250);
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
                if (headers[i] != IntPtr.Zero)
                {
                    waveInUnprepareHeader(hWave, headers[i], hdrSize);
                    Marshal.FreeHGlobal(headers[i]);
                }
                if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
            }
            waveInClose(hWave);
        }
    }

    public static void WriteWav(string path, byte[] pcm, int rate, int channels, int bits)
    {
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            int blockAlign = channels * bits / 8;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + pcm.Length);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)channels);
            w.Write(rate);
            w.Write(rate * blockAlign);
            w.Write((ushort)blockAlign);
            w.Write((ushort)bits);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(pcm.Length);
            w.Write(pcm);
        }
    }
}
'@

if ($DeviceIndex -lt 0) {
    $names = [WavRec]::ListDevices()
    $idx = -1
    for ($i = 0; $i -lt $names.Count; $i++) {
        if ($names[$i] -like "*$DeviceName*") { $idx = $i; break }
    }
    if ($idx -lt 0) {
        Write-Error "没找到名字含 '$DeviceName' 的录音设备。可用设备：`n$($names -join "`n")"
        exit 1
    }
    $DeviceIndex = $idx
}

$pcm = [WavRec]::Record($DeviceIndex, $Seconds, 16000)
[WavRec]::WriteWav($OutFile, $pcm, 16000, 1, 16)

# 顺带算一下 RMS 和峰值，方便不看波形也能判断有没有声音
$sum = 0.0; $peak = 0
for ($i = 0; $i + 1 -lt $pcm.Length; $i += 2) {
    $s = [BitConverter]::ToInt16($pcm, $i)
    $sum += [double]$s * $s
    $a = [Math]::Abs($s)
    if ($a -gt $peak) { $peak = $a }
}
$n = [Math]::Floor($pcm.Length / 2)
$rms = if ($n -gt 0) { [Math]::Sqrt($sum / $n) } else { 0 }
$rmsDb = if ($rms -gt 0) { 20 * [Math]::Log10($rms / 32768.0) } else { -120 }
$peakDb = if ($peak -gt 0) { 20 * [Math]::Log10($peak / 32768.0) } else { -120 }

Write-Host ("已保存 : {0}" -f $OutFile)
Write-Host ("时长   : {0:N2} 秒 / {1} 字节" -f ($pcm.Length / 32000.0), $pcm.Length)
Write-Host ("RMS    : {0:N1} dBFS" -f $rmsDb)
Write-Host ("峰值   : {0:N1} dBFS" -f $peakDb)
