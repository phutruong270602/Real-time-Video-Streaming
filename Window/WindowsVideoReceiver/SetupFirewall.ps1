#requires -RunAsAdministrator

param(
    [switch]$RtspOnly
)

$ErrorActionPreference = 'Stop'

$rules = @()
if (-not $RtspOnly) {
    $rules += @{
        Name = 'Windows Video Receiver - WebSocket TCP 8080'
        Protocol = 'TCP'
        Port = 8080
    }
}
$rules += @{
    Name = 'Windows Video Receiver - Bonjour mDNS UDP 5353'
    Protocol = 'UDP'
    Port = 5353
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
        -Profile Private, Domain `
        -Description 'Windows Video Receiver LAN access only; Public profile is intentionally excluded.' |
        Out-Null
}

Write-Host 'Firewall rules created for Private and Domain networks, limited to LocalSubnet.'
if ($RtspOnly) {
    Write-Host 'RTSP-only setup opens Bonjour/mDNS UDP 5353; it does not open WebSocket TCP 8080.'
}
Write-Host 'The active network must be marked Private (or Domain) for these rules to apply.'
Read-Host 'Press Enter to close'
