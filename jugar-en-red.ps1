# Levanta Ascua para jugar con otros equipos de la misma red: ./jugar-en-red.ps1
# Para apagarlo: ./jugar-en-red.ps1 -Apagar  (las cuentas y partidas se conservan)
# Guía completa: docs/jugar-en-red-local.md
param([switch]$Apagar)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$compose = @('compose', '-p', 'ascua-red', '-f', 'docker-compose.yml', '-f', 'docker-compose.red-local.yml')
# La IP de la interfaz con salida a la red; descarta las virtuales de Docker y WSL, que no tienen puerta de enlace.
$ruta = Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1
if (-not $ruta) { throw 'Este equipo no está conectado a ninguna red.' }
$ip = (Get-NetIPAddress -InterfaceIndex $ruta.InterfaceIndex -AddressFamily IPv4 | Select-Object -First 1).IPAddress
$env:ASCUA_HOST_IP = $ip

if ($Apagar) {
    docker @compose down
    exit $LASTEXITCODE
}

docker info *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker no está corriendo: abre Docker Desktop y vuelve a intentarlo.' }

docker @compose up --build -d
if ($LASTEXITCODE -ne 0) { throw 'No se pudo levantar el servidor.' }

Write-Host 'Esperando al servidor...'
$listo = $false
for ($i = 0; $i -lt 60 -and -not $listo; $i++) {
    try {
        $listo = (Invoke-WebRequest "http://localhost:8080/health/ready" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200
    } catch {
        Start-Sleep -Seconds 2
    }
}
if (-not $listo) { throw 'El servidor no respondió en 2 minutos. Revisa: docker compose -p ascua-red logs api' }

$red = Get-NetConnectionProfile -InterfaceIndex $ruta.InterfaceIndex
if ($red.NetworkCategory -eq 'Public') {
    Write-Warning 'Windows marca esta red como pública y puede bloquear a los demás. Revisa el paso del firewall en docs/jugar-en-red-local.md.'
}
if (-not (Get-NetFirewallRule -DisplayName 'Ascua red local' -ErrorAction SilentlyContinue)) {
    Write-Warning 'Falta abrir el puerto 8080 en el firewall. Revisa el paso del firewall en docs/jugar-en-red-local.md.'
}

Write-Host ''
Write-Host 'Listo. Pásales esta dirección a todos, también úsala tú:' -ForegroundColor Green
Write-Host "    http://${ip}:8080" -ForegroundColor Green
Write-Host ''
Write-Host 'Para apagarlo: ./jugar-en-red.ps1 -Apagar'
