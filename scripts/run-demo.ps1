<#
.SYNOPSIS
  Запускает BiStudent: сервер на порту 5080 (доступен в локальной сети) и клиент преподавателя (WinForms).
.PARAMETER Demo
  Запустить без облака (Firebase выключен) и на пустой базе заполнить её демонстрационными данными
  (преподаватель teacher / teacher123, группы, расписание, история посещаемости и оценок).
  Без этого ключа создаётся администратор admin / admin, пароль нужно сменить при первом входе.
.PARAMETER Reset
  Удалить базу сервера перед запуском (C:\ProgramData\BiStudent-Cloud, с -Demo — C:\ProgramData\BiStudent).
  Все данные на этом компьютере будут потеряны; данные в облаке Firebase не удаляются.
#>
param([switch]$Demo, [switch]$Reset)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root 'BiStudent.Server'
$client = Join-Path $root 'BiStudent'
$port = 5080
$url = "http://localhost:$port"

if (-not (dotnet --list-sdks | Select-String '^10\.')) {
    Write-Error 'Нужен .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0'
}

if ($Reset) {
    $data = Join-Path $env:ProgramData ($(if ($Demo) { 'BiStudent' } else { 'BiStudent-Cloud' }))
    if (Test-Path $data) { Remove-Item -Recurse -Force $data; Write-Host "База удалена: $data" }
}

Write-Host 'Сборка решения…'
dotnet build (Join-Path $root 'BiStudent.slnx') -c Release -v q | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Demo) {
    # С Firebase демо-данные ушли бы в общую облачную базу, поэтому демо работает только локально.
    $env:Demo__Enabled = 'true'
    # Реальное расписание и списки групп (seed/schedule.local.xlsx, seed/students.local.csv) подключаются, если лежат рядом.
    $env:Demo__UseLocalSchedule = 'true'
} else {
    Remove-Item Env:Demo__Enabled, Env:Demo__UseLocalSchedule -ErrorAction SilentlyContinue
}

Write-Host "Запуск сервера на порту $port…"
$serverArgs = @('run', '--project', $server, '-c', 'Release', '--no-build', '--no-launch-profile', '--urls', "http://*:$port")
if ($Demo) { $serverArgs += '--Firebase:ApiKey=' }
$serverProcess = Start-Process dotnet -ArgumentList $serverArgs `
    -WorkingDirectory $server -WindowStyle Minimized -PassThru

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        if ((Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Seconds 1 }
}
if (-not $ready) {
    Stop-Process -Id $serverProcess.Id -ErrorAction SilentlyContinue
    Write-Error 'Сервер не ответил за 60 секунд.'
}

Write-Host 'Сервер готов.'
Write-Host "Сайт регистрации лица для студентов: http://<IP этого компьютера>:$port/register/ (адрес и QR-код — в клиенте: Группы → Код регистрации)."
if ($Demo) { Write-Host 'Демонстрационный вход: teacher / teacher123.' }
else { Write-Host 'Первый вход: admin / admin — система попросит сменить пароль.' }

$exe = Get-ChildItem (Join-Path $client 'bin\Release') -Recurse -Filter BiStudent.exe | Select-Object -First 1
$clientProcess = Start-Process $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
$clientProcess.WaitForExit()

Write-Host 'Клиент закрыт, останавливаю сервер.'
Stop-Process -Id $serverProcess.Id -ErrorAction SilentlyContinue
Get-Process BiStudent.Server -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue
