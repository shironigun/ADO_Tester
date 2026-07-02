$rid = 968
do { Start-Sleep -Seconds 10; $r = Invoke-RestMethod -Uri "http://localhost:5250/api/qaloop/runs/detail/$rid" } while ($r.status -in @('Queued','Running'))
Write-Host "status=$($r.status) durationMs=$($r.durationMs)"
$ev = $r.eventLogJson | ConvertFrom-Json
$hbs = $ev | Where-Object { $_.text -match 'Still running' }
Write-Host "heartbeat events: $($hbs.Count)"
$hbs | ForEach-Object { Write-Host "  $($_.text)" }
