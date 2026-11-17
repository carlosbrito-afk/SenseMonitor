@echo off
setlocal
set "ROOT=%~dp0"
pushd "%ROOT%"
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=all"
set "COMMON=/nologo /platform:x64 /optimize+ /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll"
set "SRCLIB="
if exist "src\Core\*.cs" set "SRCLIB=%SRCLIB% src\Core\*.cs"
if exist "src\Platform\*.cs" set "SRCLIB=%SRCLIB% src\Platform\*.cs"
if not exist bin mkdir bin
set "RC=0"
if /i "%TARGET%"=="tests" goto :tests
if /i "%TARGET%"=="probe" goto :probe
if /i "%TARGET%"=="app" goto :app
if /i "%TARGET%"=="all" goto :tests
echo Uso: build.cmd [app^|tests^|probe^|all]
set "RC=2"
goto :done

:tests
if exist "tests\*.cs" (
  echo Compilando bin\ForjaTests.exe
  "%CSC%" %COMMON% /target:exe /out:bin\ForjaTests.exe %SRCLIB% tests\*.cs
  if errorlevel 1 set "RC=1"
)
if /i "%TARGET%"=="tests" goto :done

:probe
if exist "tools\Probe\*.cs" (
  echo Compilando bin\ForjaProbe.exe
  "%CSC%" %COMMON% /target:exe /out:bin\ForjaProbe.exe %SRCLIB% tools\Probe\*.cs
  if errorlevel 1 set "RC=1"
)
if /i "%TARGET%"=="probe" goto :done

:app
if exist "src\App\*.cs" (
  echo Compilando bin\SenseMonitor.exe
  "%CSC%" %COMMON% /target:winexe /win32manifest:src\App\app.manifest /win32icon:src\App\forja.ico /out:bin\SenseMonitor.exe %SRCLIB% src\App\*.cs src\App\Controls\*.cs
  if errorlevel 1 set "RC=1"
  copy /y src\App\app.config bin\SenseMonitor.exe.config >nul
)
goto :done

:done
popd
exit /b %RC%
