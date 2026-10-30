param(
    [Parameter(Mandatory = $true)]
    [string]$Title,

    [string]$Message = "",

    [string]$App = "PowerShell",

    [ValidateSet("info", "success", "warning", "error", "timer")]
    [string]$Kind = "info",

    [ValidateRange(1.5, 30)]
    [double]$Seconds = 6
)

$payload = @{
    action  = "notify"
    title   = $Title
    message = $Message
    app     = $App
    kind    = $Kind
    seconds = $Seconds
} | ConvertTo-Json -Compress

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    ".",
    "WinIsland",
    [System.IO.Pipes.PipeDirection]::Out
)

try {
    $pipe.Connect(1500)
    $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false))
    $writer.WriteLine($payload)
    $writer.Flush()
    $writer.Dispose()
}
catch {
    Write-Error "WinIsland is not running (named pipe unavailable): $_"
}
finally {
    $pipe.Dispose()
}
