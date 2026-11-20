param(
    [string]$Provider = "my-cli",
    [string]$Name = "我的 CLI",
    [string]$Label = "5 小时",
    [double]$Percent = 50,
    [string]$ResetAt = "",
    [double]$Balance = 0,
    [string]$Currency = "CNY"
)

# Pushes one usage window to the island over the named pipe.
# Example:
#   ./Usage-Island.ps1 -Provider opencode-go -Name "OpenCode Go" -Label "5 小时" -Percent 62 -ResetAt "2026-11-20T20:00:00Z"

$payload = @{
    action   = "usage"
    provider = $Provider
    name     = $Name
    label    = $Label
    percent  = $Percent
    balance  = $Balance
    currency = $Currency
}

if ($ResetAt) {
    $payload.resetAt = $ResetAt
}

$json = $payload | ConvertTo-Json -Compress
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'WinIsland', [System.IO.Pipes.PipeDirection]::Out)
try {
    $pipe.Connect(1500)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.WriteLine($json)
    $writer.Flush()
    $writer.Dispose()
    Write-Host "sent: $json"
}
finally {
    $pipe.Dispose()
}
