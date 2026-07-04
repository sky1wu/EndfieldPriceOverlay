@echo off
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo 未找到 .NET SDK，请先安装 .NET 10 SDK。
  pause
  exit /b 1
)

dotnet build ".\src\EndfieldPriceOverlay\EndfieldPriceOverlay.csproj" -c Release --nologo -v:q
if errorlevel 1 (
  echo 构建失败。
  pause
  exit /b 1
)

start "" ".\src\EndfieldPriceOverlay\bin\Release\net10.0-windows\EndfieldPriceOverlay.exe"
