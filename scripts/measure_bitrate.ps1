$client = New-Object System.Net.WebClient
$stream = $client.OpenRead('http://127.0.0.1:18765/stream.mjpg')
$buf = New-Object byte[] 65536
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$total = 0
$frames = 0
while ($sw.ElapsedMilliseconds -lt 4000) {
    $n = $stream.Read($buf, 0, $buf.Length)
    if ($n -le 0) { break }
    $total += $n
}
$totalBytes = [double]$total
$secs = $sw.ElapsedMilliseconds / 1000.0
$megabits = ($totalBytes * 8.0) / 1000000.0
$mbps = $megabits / $secs
$mBps = ($totalBytes / 1048576.0) / $secs
Write-Host "Duration: $secs s"
Write-Host "Total transferred: $([Math]::Round($totalBytes / 1048576.0, 2)) MB ($total bytes)"
Write-Host "Bandwidth: $([Math]::Round($mbps, 2)) Mbps ($([Math]::Round($mBps, 2)) MB/s)"
