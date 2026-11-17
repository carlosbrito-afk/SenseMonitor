# SenseMonitor deploy: close the running app gracefully, build, test, install to
# %ProgramFiles%\SenseMonitor, point the autostart task there and start it through the task.
# Run from an ELEVATED PowerShell:
#   powershell -ExecutionPolicy Bypass -File C:\claude\forja\deploy.ps1 [-Repo C:\claude\forja] [-NoStart]
# Every step is logged to <repo>\deploy.log. Nothing is ever force-killed: the app must hand the
# fans back to the firmware on exit, so a close that does not finish aborts the deploy.
# If the build or the tests fail nothing is started (fix and rerun); if a later step fails after
# a good build, the installed copy is started again through the task (best effort).

param(
    [string]$Repo = "",
    [switch]$NoStart
)

# Native tools write progress to stderr; in Windows PowerShell 5.1 that must never become a
# terminating error. Failures are detected from exit codes and caught explicitly instead.
$ErrorActionPreference = 'Continue'

if ([string]::IsNullOrEmpty($Repo)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    if (Test-Path (Join-Path $here 'build.cmd')) { $Repo = $here } else { $Repo = 'C:\claude\forja' }
}

$sys      = Join-Path $env:SystemRoot 'System32'
$taskkill = Join-Path $sys 'taskkill.exe'
$schtasks = Join-Path $sys 'schtasks.exe'
$cmdExe   = Join-Path $sys 'cmd.exe'
$install  = Join-Path $env:ProgramFiles 'SenseMonitor'
$exeName  = 'SenseMonitor.exe'
$log      = Join-Path $Repo 'deploy.log'
$script:buildOk = $false
$script:nativeOut = @()

function Log([string]$msg) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $msg
    try { Add-Content -Path $log -Value $line -Encoding UTF8 -ErrorAction Stop } catch { }
    Write-Host $line
}

# Runs a native tool; stdout+stderr are logged as plain text, the exit code is returned.
function Invoke-Native([string]$exe, [string[]]$argList) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $script:nativeOut = @(& $exe @argList 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    } catch {
        $script:nativeOut = @('falha ao executar ' + $exe + ': ' + $_.Exception.Message)
        $code = -1
    } finally {
        $ErrorActionPreference = $prev
    }
    return $code
}

# Signals the running app's exit event (Administrators/SYSTEM only may open it; this script runs elevated).
# $true = the event was there and is set; $false = no event (no app, an older app) or no access.
function Send-ExitEvent {
    try {
        $ev = [System.Threading.EventWaitHandle]::OpenExisting('Global\SenseMonitor.Sair', [System.Security.AccessControl.EventWaitHandleRights]::Modify)
        try { return [bool]$ev.Set() } finally { $ev.Close() }
    } catch {
        return $false
    }
}

function Log-Native([int]$maxLines) {
    $lines = $script:nativeOut
    if ($maxLines -gt 0 -and $lines.Count -gt $maxLines) { $lines = $lines | Select-Object -Last $maxLines }
    foreach ($l in $lines) { Log ('  ' + $l) }
}

# Starts the installed copy again through the task; only after a good build + tests.
function Start-Installed {
    if ($NoStart -or -not $script:buildOk) { return }
    if (Test-Path (Join-Path $install $exeName)) {
        Log 'iniciando a copia instalada pela tarefa agendada'
        [void](Invoke-Native $schtasks @('/Run', '/TN', 'SenseMonitor'))
        Log-Native 0
    }
}

function Fail([string]$msg) {
    Log ('ERRO: ' + $msg)
    Start-Installed
    exit 1
}

try {
    Log ('--- deploy em ' + $Repo + ' ---')

    # 0. elevation
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Fail 'rode este script num PowerShell aberto como administrador'
    }
    if (-not (Test-Path (Join-Path $Repo 'build.cmd'))) { Fail ('build.cmd nao encontrado em ' + $Repo) }

    # 1. graceful close: the app's named exit event first (the same one `SenseMonitor.exe --sair` signals), then, only if the app
    #    is still there after 10 s (or the event does not exist: an older app), taskkill WITHOUT /F, which posts WM_CLOSE.
    #    Either way the app hands the fans back and exits. The event is signalled from here and not through the installed exe's
    #    --sair, because the installed exe may predate that switch (an older exe would show an "invalid arguments" box).
    $running = @(Get-Process -Name 'SenseMonitor' -ErrorAction SilentlyContinue)
    $t0 = Get-Date
    $signalled = $false
    if ($running.Count -gt 0) {
        $signalled = Send-ExitEvent
        Log ('pedido de saida pelo evento Global\SenseMonitor.Sair: ' + $(if ($signalled) { 'enviado' } else { 'sem evento (app antigo ou sem acesso)' }))
    }
    $closeAt = 10
    if (-not $signalled) { $closeAt = 0 }
    $asked = ($running.Count -eq 0)
    while ((Get-Process -Name 'SenseMonitor' -ErrorAction SilentlyContinue) -and ((Get-Date) - $t0).TotalSeconds -lt 30) {
        if (-not $asked -and ((Get-Date) - $t0).TotalSeconds -ge $closeAt) {
            $asked = $true
            foreach ($p in @(Get-Process -Name 'SenseMonitor' -ErrorAction SilentlyContinue)) {
                Log ('fechando o app (pid ' + $p.Id + ', sem /F)')
                [void](Invoke-Native $taskkill @('/PID', [string]$p.Id))
                Log-Native 0
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Name 'SenseMonitor' -ErrorAction SilentlyContinue) {
        Fail 'o app nao fechou em 30 s; feche pelo menu do icone (Sair) e rode de novo (nada foi forcado)'
    }
    if ($running.Count -gt 0) { Log ('app fechado em ' + [int]((Get-Date) - $t0).TotalMilliseconds + ' ms') }

    # 2. build (failure: nothing is started)
    Log 'compilando (build.cmd all)'
    $rc = Invoke-Native $cmdExe @('/d', '/c', ('"' + (Join-Path $Repo 'build.cmd') + '" all'))
    Log-Native 0
    if ($rc -ne 0) { Fail ('build falhou (codigo ' + $rc + ')') }

    # 3. tests (failure: nothing is started)
    Log 'rodando os testes'
    $rc = Invoke-Native (Join-Path $Repo 'bin\ForjaTests.exe') @()
    $script:nativeOut | Where-Object { $_ -match '^FAIL' } | ForEach-Object { Log ('  ' + $_) }
    Log-Native 1
    if ($rc -ne 0) { Fail ('testes falharam (' + $rc + ')') }
    $script:buildOk = $true

    # 4. install (Program Files: only administrators can write there)
    $built = Join-Path $Repo ('bin\' + $exeName)
    if (-not (Test-Path $built)) { Fail ('nao achei ' + $built) }
    try {
        if (-not (Test-Path $install)) {
            New-Item -ItemType Directory -Path $install -ErrorAction Stop | Out-Null
            Log ('pasta criada: ' + $install)
        }
        $item = Get-Item $install -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail ($install + ' e um link/juncao; recusado') }
        Copy-Item -Path $built -Destination (Join-Path $install $exeName) -Force -ErrorAction Stop
        Log ('copiado: ' + $exeName)
        $cfg = Join-Path $Repo ('bin\' + $exeName + '.config')
        if (Test-Path $cfg) {
            Copy-Item -Path $cfg -Destination (Join-Path $install ($exeName + '.config')) -Force -ErrorAction Stop
            Log ('copiado: ' + $exeName + '.config')
        }
        # The manifest and the icon are embedded in the exe; nothing else to copy.
        $tmp = Join-Path $install 'tmp'
        if (-not (Test-Path $tmp)) { New-Item -ItemType Directory -Path $tmp -ErrorAction Stop | Out-Null }
    } catch {
        Fail ('nao foi possivel instalar em ' + $install + ': ' + $_.Exception.Message)
    }

    # 5. autostart -> installed copy (the app's own code builds the task XML in the protected tmp folder)
    Log 'registrando a inicializacao automatica (--registrar-inicio)'
    try {
        $reg = Start-Process -FilePath (Join-Path $install $exeName) -ArgumentList '--registrar-inicio' -Wait -PassThru -ErrorAction Stop
        $regCode = $reg.ExitCode
    } catch {
        $regCode = -1
        Log ('  ' + $_.Exception.Message)
    }
    if ($regCode -ne 0) { Fail ('nao foi possivel registrar a tarefa (codigo ' + $regCode + '); veja %APPDATA%\SenseMonitor\erro.log') }
    [void](Invoke-Native $schtasks @('/Query', '/TN', 'SenseMonitor', '/XML'))
    $script:nativeOut | Where-Object { $_ -match '<Command>' } | ForEach-Object { Log ('  tarefa: ' + $_.Trim()) }
    # the fan sentinel's own on-demand task (registered by the same --registrar-inicio)
    [void](Invoke-Native $schtasks @('/Query', '/TN', 'SenseMonitor-Sentinela', '/XML'))
    $script:nativeOut | Where-Object { $_ -match '<Command>|<Arguments>' } | ForEach-Object { Log ('  tarefa do guardiao: ' + $_.Trim()) }

    # 6. start through the task (elevated, no UAC prompt)
    if ($NoStart) { Log 'nao iniciado (-NoStart)'; Log '--- fim ---'; exit 0 }
    Log 'iniciando pela tarefa agendada'
    $rc = Invoke-Native $schtasks @('/Run', '/TN', 'SenseMonitor')
    Log-Native 0
    if ($rc -ne 0) { Log ('ERRO: schtasks /Run falhou (codigo ' + $rc + ')'); exit 1 }
    Start-Sleep -Seconds 4
    $now = @(Get-Process -Name 'SenseMonitor' -ErrorAction SilentlyContinue)
    if ($now.Count -eq 0) { Log 'ERRO: o app nao apareceu depois de schtasks /Run'; exit 1 }
    foreach ($p in $now) {
        $path = ''
        try { $path = $p.Path } catch { }
        Log ('rodando: pid ' + $p.Id + ' ' + $path)
    }
    Log '--- fim ---'
    exit 0
} catch {
    Fail ('erro inesperado: ' + $_.Exception.Message)
}
