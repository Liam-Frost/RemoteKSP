@echo off
setlocal
cd /d "%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -property installationPath`) do set "VSDIR=%%i"
for %%a in (x64 x86) do (
    call :build %%a
    if errorlevel 1 exit /b 1
)
exit /b 0
:build
setlocal
call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" %1 >nul
if errorlevel 1 exit /b 1
cl /nologo /O2 /W3 /EHsc /MD /utf-8 /DUNICODE /D_UNICODE HttpPinProbe.cpp ../../native/ksp/http.cpp /link bcrypt.lib crypt32.lib winhttp.lib advapi32.lib /OUT:HttpPinProbe_%1.exe
exit /b %errorlevel%
