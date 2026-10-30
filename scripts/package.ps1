<#
.SYNOPSIS
  Собирает dist/BiStudent-win-x64.zip: распаковал, запустил BiStudent.exe, вошёл.
  В корне архива клиент одним файлом и модели, в папке Server встроенный сервер, который клиент запускает сам.
#>
param([string]$Output = 'dist', [string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'out\BiStudent'
$output = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$zip = Join-Path $output 'BiStudent-win-x64.zip'
# Нативные библиотеки (OpenCV, SQLite) лежат рядом с exe: при самораспаковке первый запуск ждал распаковки ~200 МБ во временную папку.
# ReadyToRun компилирует код заранее, чтобы клиент и сервер не тратили секунды на JIT при каждом старте.
$single = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:PublishReadyToRun=true', '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$Version")

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
dotnet publish (Join-Path $root 'BiStudent\BiStudent.csproj') @single -o $stage
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $root 'BiStudent.Server\BiStudent.Server.csproj') @single -o (Join-Path $stage 'Server')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

@'
BiStudent — электронный журнал преподавателя

1. Основной способ: запустите BiStudent.Desktop-win-Setup.exe. Программа установится без прав
   администратора, создаст ярлык на рабочем столе и будет обновляться сама.
2. Переносная версия: распакуйте BiStudent.Desktop-win-Portable.zip целиком в любую папку
   (не запускайте из самого архива), затем запустите BiStudent.exe. Она тоже обновляется сама.
3. Архив BiStudent-win-x64.zip для ручного запуска: распакуйте его целиком в любую папку
   и запустите BiStudent.exe.
4. Войдите логином и паролем, которые выдал администратор. Нужен интернет: при первом входе
   расписание, группы и журналы загружаются из облака, дальше программа работает и без сети.

Нужна Windows 10/11 x64, устанавливать .NET не нужно. Папки Models и Server должны лежать рядом с BiStudent.exe.
Если Windows покажет «Windows защитила ваш компьютер», нажмите «Подробнее» → «Выполнить в любом случае».
Если брандмауэр спросит про BiStudent.Server, разрешите доступ: через него студенты регистрируют лица с телефона.
Данные хранятся в C:\ProgramData\BiStudent-Cloud и в облаке, при обновлении программы не пропадают.
'@ | Set-Content (Join-Path $stage 'README.txt') -Encoding utf8

New-Item -ItemType Directory -Force $output | Out-Null
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Готово: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) МБ)"
