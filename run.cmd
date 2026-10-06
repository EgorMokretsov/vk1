@echo off
dotnet run --project "%~dp0src\WukongBench\WukongBench.csproj" -c Release -- %*
exit /b %errorlevel%
