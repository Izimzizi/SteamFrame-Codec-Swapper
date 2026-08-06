@echo off
echo Building SteamLinkVRSettings...
"%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" SteamLinkVRSettings\SteamLinkVRSettings.csproj -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
if %errorlevel% neq 0 (
    echo Build failed!
    pause
    exit /b %errorlevel%
)
echo.
echo Build succeeded! Output:
dir /b SteamLinkVRSettings\bin\x64\Release\net9.0-windows10.0.22621.0\SteamLinkVRSettings.exe
echo.
echo Run with: SteamLinkVRSettings\bin\x64\Release\net9.0-windows10.0.22621.0\SteamLinkVRSettings.exe
pause
