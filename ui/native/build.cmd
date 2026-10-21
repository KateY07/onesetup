@echo off
rem Builds onesetup-ui.exe (x64) with MSVC. Usage: build.cmd [output.exe]   (default: ..\publish\onesetup-ui.exe)
setlocal
set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvars64.bat"
call "%VCVARS%" >nul || exit /b 1
set "EXE=%~1"
if "%EXE%"=="" set "EXE=%~dp0..\publish\onesetup-ui.exe"
for %%F in ("%EXE%") do if not exist "%%~dpF" mkdir "%%~dpF"
pushd "%~dp0"
rc /nologo /fo "%TEMP%\onesetup-ui.res" app.rc || exit /b 1
cl /nologo /std:c++17 /O2 /MT /EHsc /W3 /utf-8 /DUNICODE /D_UNICODE main.cpp "%TEMP%\onesetup-ui.res" /Fe:"%EXE%" /Fo:"%TEMP%\onesetup-ui.obj" /link /SUBSYSTEM:WINDOWS delayimp.lib /DELAYLOAD:d3d11.dll /DELAYLOAD:dxgi.dll /DELAYLOAD:d2d1.dll /DELAYLOAD:dwrite.dll /DELAYLOAD:dcomp.dll /DELAYLOAD:dwmapi.dll /DELAYLOAD:user32.dll /DELAYLOAD:gdi32.dll /DELAYLOAD:shell32.dll /OPT:REF /OPT:ICF /MANIFEST:EMBED /MANIFESTINPUT:app.manifest
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%
