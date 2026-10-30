@echo off
setlocal enabledelayedexpansion

cd /d "%~dp0"

:: Locate Visual Studio
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
:: vcvarsall.bat calls vswhere by name
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
if not exist "%VSWHERE%" (
    echo ERROR: vswhere.exe not found. Install Visual Studio 2022.
    exit /b 1
)

for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -property installationPath`) do set "VSDIR=%%i"
if not exist "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" (
    echo ERROR: vcvarsall.bat not found in %VSDIR%
    exit /b 1
)

:: /Brepro：可重现构建，不写入时间戳。源码不变时 DLL 字节完全相同，
:: 客户端按内容比较已安装的 KSP，升级客户端不会误报"需要更新"
:: Build x64
echo.
echo === Building x64 ===
setlocal
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul
if errorlevel 1 exit /b 1

cl /O2 /W3 /LD /MD /utf-8 /Brepro ^
   /DUNICODE /D_UNICODE ^
   ksp_main.cpp http.cpp register.cpp ^
   /link /DEF:RemoteKSP.def /Brepro ^
   bcrypt.lib crypt32.lib winhttp.lib advapi32.lib ^
   /OUT:RemoteKSP.dll
if errorlevel 1 exit /b 1
move /Y RemoteKSP.dll RemoteKSP_x64.dll >nul
endlocal

:: Build x86
echo.
echo === Building x86 ===
setlocal
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul
if errorlevel 1 exit /b 1

cl /O2 /W3 /LD /MD /utf-8 /Brepro ^
   /DUNICODE /D_UNICODE ^
   ksp_main.cpp http.cpp register.cpp ^
   /link /DEF:RemoteKSP.def /Brepro ^
   bcrypt.lib crypt32.lib winhttp.lib advapi32.lib ^
   /OUT:RemoteKSP.dll
if errorlevel 1 exit /b 1
move /Y RemoteKSP.dll RemoteKSP_x86.dll >nul
endlocal

echo.
echo Build complete: RemoteKSP_x64.dll, RemoteKSP_x86.dll
