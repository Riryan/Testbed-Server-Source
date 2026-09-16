@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: .NET 8 SDK was not found in PATH.
  exit /b 10
)

echo Publishing MMO Dashboard for Windows x64...
dotnet publish "MMODashboard.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "Publish"
if errorlevel 1 exit /b %errorlevel%

echo.
echo Publish complete: %~dp0Publish
exit /b 0
