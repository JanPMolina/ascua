<#
.SYNOPSIS
  Prueba de carga reproducible de Ascua (docs/rendimiento.md). Equivalente a loadtests/run.sh.
.EXAMPLE
  ./loadtests/run.ps1 -Stages 10,20,40,80 -StageSeconds 60 -Label rampa
.EXAMPLE
  ./loadtests/run.ps1 -Url https://ascua.example.azurecontainerapps.io -Stages 20 -StageSeconds 120
#>
param(
    [string] $Url = "",
    [int[]] $Stages = @(10),
    [int] $StageSeconds = 60,
    [int] $IdleSeconds = 15,
    [int] $ThinkMs = 2000,
    [double] $SkipRate = 0,
    [string] $Label = "carga"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$run = "{0}-{1}" -f [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss"), $Label
$reports = Join-Path (Get-Location) "loadtests/reports"
New-Item -ItemType Directory -Force (Join-Path $reports $run) | Out-Null
# Proyecto propio: no toca la base de desarrollo, y cada corrida parte de cero.
$compose = @("compose", "-p", "ascua-carga", "-f", "docker-compose.yml", "-f", "loadtests/docker-compose.load.yml")
$common = @("--label", $Label, "--out", "/reports/$run", "--stages", ($Stages -join ","), "--stage-seconds", $StageSeconds,
    "--idle-seconds", $IdleSeconds, "--think-ms", $ThinkMs, "--skip-rate", $SkipRate.ToString([Globalization.CultureInfo]::InvariantCulture))

if ($Url -eq "") {
    docker @compose down -v --remove-orphans
    docker @compose up -d --build --wait api
    docker @compose --profile load build loadtest
    $apiId = (docker @compose ps -q api).Trim()
    $stats = Join-Path $reports "$run/stats.csv"
    # Una muestra por ciclo de docker stats (~1-2 s): epochMs,CPU%,uso / límite
    $sampler = Start-Job -ArgumentList $apiId, $stats -ScriptBlock {
        param($id, $file)
        while ($true) {
            $line = docker stats --no-stream --format "{{.CPUPerc}},{{.MemUsage}}" $id
            if ($LASTEXITCODE -ne 0) { break }
            Add-Content -Path $file -Value ("{0},{1}" -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $line)
        }
    }
    try {
        docker @compose --profile load run --rm loadtest --url http://api:8080 --stats "/reports/$run/stats.csv" @common
        $code = $LASTEXITCODE
    }
    finally {
        Stop-Job $sampler; Remove-Job $sampler
    }
}
else {
    docker build -t ascua-loadtest -f loadtests/Dockerfile .
    docker run --rm -v "${reports}:/reports" ascua-loadtest --url $Url --clock estimate @common
    $code = $LASTEXITCODE
}

Write-Host "Resultados en loadtests/reports/$run/resumen.md (código $code: 0 = cumple la meta en la primera etapa)"
exit $code
