<#
.SYNOPSIS
  列出 / 切换 Windows 默认录音设备，用于把 VB-CABLE 的 "CABLE Output" 变成系统麦克风。

.DESCRIPTION
  通过未公开的 IPolicyConfig COM 接口设置默认端点（Console / Multimedia / Communications 三种角色）。
  端点 ID 直接从注册表 MMDevices\Audio\Capture 读取，无需 IMMDeviceEnumerator。

.EXAMPLE
  .\Set-DefaultMic.ps1 -List
  .\Set-DefaultMic.ps1 -Name 'CABLE Output'
  .\Set-DefaultMic.ps1 -Restore
#>
[CmdletBinding(DefaultParameterSetName = 'List')]
param(
    [Parameter(ParameterSetName = 'List')]
    [switch]$List,

    [Parameter(ParameterSetName = 'Set', Mandatory = $true)]
    [string]$Name,

    [Parameter(ParameterSetName = 'Restore')]
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$CaptureRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
$PKEY_Device_FriendlyName = '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
$StateFile = Join-Path $env:LOCALAPPDATA 'MicCast\default-mic.txt'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class CPolicyConfigVistaClient { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr fmt);
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool bDefault, out IntPtr fmt);
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endPoint, IntPtr mixFormat);
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool bDefault, out long a, out long b);
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr mode);
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr store, out IntPtr value);
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr store, IntPtr value);
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, ERole role, out IMMDevice endpoint);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int access, out IntPtr store);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
}

public static class DefaultAudioDevice
{
    private const int ECapture = 1;

    public static void SetDefault(string endpointId)
    {
        var policy = (IPolicyConfig)new CPolicyConfigVistaClient();
        foreach (ERole role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
        {
            int hr = policy.SetDefaultEndpoint(endpointId, role);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>返回指定角色的当前默认录音端点 ID；没有任何设备时返回 null。</summary>
    public static string GetDefault(ERole role)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        IMMDevice device;
        int hr = enumerator.GetDefaultAudioEndpoint(ECapture, role, out device);
        if (hr != 0 || device == null) return null;

        string id;
        return device.GetId(out id) == 0 ? id : null;
    }
}
'@

function Get-CaptureEndpoints {
    Get-ChildItem $CaptureRoot -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Join-Path $_.PSPath 'Properties'
        if (-not (Test-Path $props)) { return }
        $friendly = $null
        try { $friendly = (Get-ItemProperty $props -Name $PKEY_Device_FriendlyName -ErrorAction Stop).$PKEY_Device_FriendlyName } catch { return }
        if ([string]::IsNullOrWhiteSpace($friendly)) { return }

        $state = $null
        try { $state = (Get-ItemProperty $_.PSPath -Name DeviceState -ErrorAction Stop).DeviceState } catch { }

        [pscustomobject]@{
            Name       = $friendly
            # 注册表子键只有 GUID，实际端点 ID 需要带 {0.0.1.00000000}. 前缀（录音设备固定 0.0.1）
            EndpointId = '{0.0.1.00000000}.' + $_.PSChildName
            State      = $state
            Active     = ($state -eq 1)
        }
    }
}

function Resolve-Endpoint([string]$pattern) {
    $all = @(Get-CaptureEndpoints)
    $hit = @($all | Where-Object { $_.Name -eq $pattern })
    if ($hit.Count -eq 0) { $hit = @($all | Where-Object { $_.Name -like "*$pattern*" }) }
    if ($hit.Count -eq 0) { throw "找不到录音设备: $pattern" }
    if ($hit.Count -gt 1) {
        throw ("匹配到多个录音设备，请写更完整的名字: " + ($hit.Name -join ' | '))
    }
    return $hit[0]
}

switch ($PSCmdlet.ParameterSetName) {
    'List' {
        $current = [DefaultAudioDevice]::GetDefault([ERole]::Console)
        Get-CaptureEndpoints |
            Sort-Object @{ Expression = { -not $_.Active } }, Name |
            Format-Table @{ n = '名称'; e = { $_.Name } },
                         @{ n = '可用'; e = { $_.Active } },
                         @{ n = '默认'; e = { if ($_.EndpointId -eq $current) { '<==' } else { '' } } },
                         EndpointId -AutoSize
    }
    'Set' {
        $target = Resolve-Endpoint $Name
        if (-not $target.Active) { throw "设备未激活（DeviceState=$($target.State)），无法设为默认: $($target.Name)" }

        $original = [DefaultAudioDevice]::GetDefault([ERole]::Console)
        if (-not $original) { throw '无法读取当前默认录音设备。' }
        if ($original -eq $target.EndpointId) {
            Write-Host "默认录音设备已经是: $($target.Name)"
            return
        }

        # 只在没有记录时写一次，连续切换后 -Restore 仍能回到最初的设备
        if (-not (Test-Path $StateFile)) {
            $originalName = (@(Get-CaptureEndpoints) | Where-Object { $_.EndpointId -eq $original }).Name
            New-Item -ItemType Directory -Path (Split-Path $StateFile) -Force | Out-Null
            Set-Content -Path $StateFile -Value $original -Encoding ASCII
            Write-Host "已记录原默认录音设备: $(if ($originalName) { $originalName } else { $original })"
        }

        [DefaultAudioDevice]::SetDefault($target.EndpointId)
        Write-Host "默认录音设备已切换为: $($target.Name)"
    }
    'Restore' {
        if (-not (Test-Path $StateFile)) { throw "没有记录原始设备（$StateFile 不存在），请用 -Name 手动指定。" }
        $prevId = (Get-Content $StateFile | Select-Object -First 1).Trim()
        $prev = @(Get-CaptureEndpoints) | Where-Object { $_.EndpointId -eq $prevId }
        if (-not $prev) { throw "原设备已不存在: $prevId" }
        if (-not $prev.Active) { throw "原设备未激活: $($prev.Name)" }

        [DefaultAudioDevice]::SetDefault($prev.EndpointId)
        Remove-Item $StateFile -Force
        Write-Host "默认录音设备已恢复为: $($prev.Name)"
    }
}
