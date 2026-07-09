@echo off
echo Building Overlord...
dotnet build Overlord.csproj -c Release
if %ERRORLEVEL% NEQ 0 (
    echo BUILD FAILED
    pause
    exit /b 1
)
echo Build succeeded: Assemblies\Overlord.dll
echo.

set RIMWORLD_MODS=C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods
if exist "%RIMWORLD_MODS%" (
    echo Copying to RimWorld Mods folder...
    xcopy /E /I /Y "About" "%RIMWORLD_MODS%\Overlord\About"
    xcopy /E /I /Y "Assemblies" "%RIMWORLD_MODS%\Overlord\Assemblies"
    if exist "Defs" xcopy /E /I /Y "Defs" "%RIMWORLD_MODS%\Overlord\Defs"
    if exist "relay-server\public" xcopy /E /I /Y "relay-server\public" "%RIMWORLD_MODS%\Overlord\WebUI"
    echo Installed to %RIMWORLD_MODS%\Overlord
) else (
    echo RimWorld Mods folder not found, skipping install
)
pause
