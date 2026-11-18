# Windows Firewall rules for the internal calls (H176) — a TEMPLATE: review the scopes, then run elevated on the
# server it belongs to. Every rule is limited to the hospital's private ranges; nothing is opened to the internet.
# (On Linux servers the same ports apply: see README.md section 4.)

param([ValidateSet('app', 'turn', 'livekit')] [string] $Role = 'app')

$Hospital = @('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')   # narrow to the departments' real subnets

switch ($Role) {
    'app' {
        New-NetFirewallRule -DisplayName 'CrossBuy calls - HTTPS + SignalR' -Direction Inbound -Protocol TCP -LocalPort 443 -RemoteAddress $Hospital -Action Allow
    }
    'turn' {
        New-NetFirewallRule -DisplayName 'Hospital TURN - 3478 UDP' -Direction Inbound -Protocol UDP -LocalPort 3478 -RemoteAddress $Hospital -Action Allow
        New-NetFirewallRule -DisplayName 'Hospital TURN - 3478 TCP' -Direction Inbound -Protocol TCP -LocalPort 3478 -RemoteAddress $Hospital -Action Allow
        New-NetFirewallRule -DisplayName 'Hospital TURN - 5349 TLS' -Direction Inbound -Protocol TCP -LocalPort 5349 -RemoteAddress $Hospital -Action Allow
        New-NetFirewallRule -DisplayName 'Hospital TURN - relay ports' -Direction Inbound -Protocol UDP -LocalPort '49152-65535' -RemoteAddress $Hospital -Action Allow
    }
    'livekit' {
        New-NetFirewallRule -DisplayName 'Hospital LiveKit - wss (nginx)' -Direction Inbound -Protocol TCP -LocalPort 443 -RemoteAddress $Hospital -Action Allow
        New-NetFirewallRule -DisplayName 'Hospital LiveKit - media TCP' -Direction Inbound -Protocol TCP -LocalPort 7881 -RemoteAddress $Hospital -Action Allow
        New-NetFirewallRule -DisplayName 'Hospital LiveKit - media UDP' -Direction Inbound -Protocol UDP -LocalPort '50000-60000' -RemoteAddress $Hospital -Action Allow
    }
}
