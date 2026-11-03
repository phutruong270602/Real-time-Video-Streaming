#requires -RunAsAdministrator

param(
    [switch]$RtspOnly
)

$ErrorActionPreference = 'Stop'

$rules = @()
if ($RtspOnly) {
    $rules += @{
        Name = 'Windows RTSP Receiver - UDP Broadcast Discovery 8555'
        Protocol = 'UDP'
        Port = 8555
        Profile = 'Any'
    }
} else {
    $rules += @{
        Name = 'Windows Video Receiver - WebSocket TCP 8080'
        Protocol = 'TCP'
        Port = 8080
        Profile = @('Private', 'Domain')
    }
    $rules += @{
        Name = 'Windows Video Receiver - Bonjour mDNS UDP 5353'
        Protocol = 'UDP'
        Port = 5353
        Profile = @('Private', 'Domain')
    }
}

foreach ($rule in $rules) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule

    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Direction Inbound `
        -Action Allow `
        -Protocol $rule.Protocol `
        -LocalPort $rule.Port `
        -RemoteAddress LocalSubnet `
        -Profile $rule.Profile `
        -Description 'Windows Video Receiver LAN access only.' |
        Out-Null
}

if ($RtspOnly) {
    Write-Host 'RTSP-only setup opens UDP broadcast discovery port 8555 for the local subnet on all network profiles.'
} else {
    Write-Host 'WebRTC setup opens WebSocket TCP 8080 and Bonjour/mDNS UDP 5353.'
}
Write-Host 'Firewall rules are limited to LocalSubnet.'
if (-not $RtspOnly) {
    Write-Host 'WebRTC rules apply only to Private and Domain network profiles.'
}
Read-Host 'Press Enter to close'
