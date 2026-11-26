@echo off
rem Builds bin\CubeScope.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem   build.bat        build
rem   build.bat test   build, then run the built-in self-test (it must report 0 failed)
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo The .NET Framework 4 C# compiler was not found. It comes with Windows; turn on .NET Framework 4.8 in
  echo "Turn Windows features on or off" and run build.bat again.
  exit /b 1
)
if not exist bin mkdir bin
"%CSC%" /nologo /target:exe /optimize+ /out:bin\CubeScope.exe /resource:dashboard.html,CubeScope.dashboard.html /r:System.ServiceProcess.dll /r:System.Security.dll /r:System.Core.dll *.cs
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Built bin\CubeScope.exe
if /i "%~1"=="test" (
  bin\CubeScope.exe --selftest
  if errorlevel 1 exit /b 1
)
exit /b 0
