@echo off
rem ============================================================
rem  XSD Audio Rack 一键构建并复制到桌面
rem  在 Windows 上双击运行即可
rem  NAudio 为本地 DLL 引用（libs 文件夹），断网也能编译
rem ============================================================
cd /d "%~dp0"
echo.
echo  === XSD Audio Rack: 开始构建 ===
echo.

set "DESK=%USERPROFILE%\Desktop"
if not exist "%DESK%\" set "DESK=%USERPROFILE%\OneDrive\Desktop"

rem 清理 macOS 复制产生的 ._ 垃圾文件
for /r "%~dp0" %%f in (._*) do del /f /a /q "%%f" >nul 2>&1

rem 检查 libs（NAudio 本地 DLL）
if not exist "libs\NAudio.Core.dll" (
  echo.
  echo  === 缺少 libs 文件夹（NAudio DLL）！ ===
  echo  请把 Mac 上的整个 XSDAudioRackWin 文件夹完整复制过来，特别是 libs 子文件夹。
  echo.
  pause
  exit /b 1
)

rem 清掉旧的还原缓存
if exist "obj\project.assets.json" del /q "obj\project.assets.json"

dotnet restore XSDAudioRack.csproj --ignore-failed-sources
if errorlevel 1 goto fail

dotnet publish XSDAudioRack.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%TEMP%\XSDAudioRack_pub" --no-restore
if errorlevel 1 goto fallback

copy /y "%TEMP%\XSDAudioRack_pub\XSDAudioRack.exe" "%DESK%\XSDAudioRack.exe" >nul
if errorlevel 1 goto fail

echo.
echo  === 成功！已复制到: %DESK%\XSDAudioRack.exe ===
echo.
pause
exit /b 0

:fallback
echo.
echo  === 单文件发布失败，改用普通构建并复制整个文件夹 ===
echo.
dotnet build XSDAudioRack.csproj -c Release --no-restore
if errorlevel 1 goto fail

robocopy "bin\Release\net8.0-windows" "%DESK%\XSDAudioRack" /MIR >nul
if errorlevel 8 goto fail

echo.
echo  === 成功！已复制到: %DESK%\XSDAudioRack\XSDAudioRack.exe ===
echo.
pause
exit /b 0

:fail
echo.
echo  === 构建失败！请把上面的报错信息发给开发者修复 ===
echo.
pause
exit /b 1
