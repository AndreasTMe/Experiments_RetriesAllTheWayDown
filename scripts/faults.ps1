# Apply or remove a fault by hand while the AppHost is running. Wraps `Runner faults`.
#   ./scripts/faults.ps1 postgres-down
#   ./scripts/faults.ps1 latency 15000
#   ./scripts/faults.ps1 api 0.2
#   ./scripts/faults.ps1 clear
$runner = Join-Path $PSScriptRoot 'Runner'
dotnet run --project $runner -- faults @args
