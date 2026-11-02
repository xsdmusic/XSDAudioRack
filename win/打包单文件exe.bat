@echo off
setlocal
rem All paths relative to this script (D:\AI\XSDAudioRackWin).

where dotnet >nul 2>nul
if not %errorlevel%==0 (
    echo [ERROR] dotnet SDK not found. Please install .NET 8 SDK first:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

rem bring the source image into the project on first run
if not exist "%~dp002.png" copy /Y "D:\AI\02.png" "%~dp002.png" >nul

echo Generating rounded app.ico ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make_icon.ps1"
if errorlevel 1 (
    echo [ERROR] app.ico generation failed. Check 02.png is a valid image.
    pause
    exit /b 1
)

echo.
echo Building single-file exe, this may take a few minutes...
echo.

dotnet publish "%~dp0XSDAudioRack.csproj" -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0bin\publish-single"

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. Please send the error message to Claude.
    pause
    exit /b 1
)

for /f "delims=" %%D in ('powershell -NoProfile -Command "[Environment]::GetFolderPath('Desktop')"') do set "DESKTOP=%%D"
echo Desktop path: %DESKTOP%
echo.
copy /Y "%~dp0bin\publish-single\XSDAudioRack.exe" "%DESKTOP%\XSDAudioRack.exe"
if errorlevel 1 (
    echo [ERROR] Copy to Desktop failed.
) else (
    echo Done! XSDAudioRack.exe is now on your Desktop.
    echo This exe is fully self-contained and can be distributed as-is.
)
pause
