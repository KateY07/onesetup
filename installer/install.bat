@echo off
setlocal EnableExtensions
rem OneSetup installer, packed by OneSetup itself (installer\build-installer.ps1).
rem It copies onesetup.exe to %LOCALAPPDATA%\OneSetup, adds that folder to your USER PATH, and then offers a
rem harmless demo of the installer window. Nothing outside your user profile is touched.
rem Lines starting with "." drive the installer window (see ui\README.md); everything else goes to its Details log.
rem Set ONESETUP_UNATTENDED=1 to skip the questions and the demo. Keep this file ASCII.
set "TARGET=%LOCALAPPDATA%\OneSetup"
echo .title OneSetup Setup
if not defined LOCALAPPDATA goto missing_profile
if not exist "%~dp0onesetup.exe" goto missing_files

if defined ONESETUP_UNATTENDED goto consent_given
echo .info Waking the installer up. It was napping.
echo .progress 4
echo .ask Install OneSetup for your user account and add it to PATH? Nothing outside your profile is touched.
set "ANS="
set /p ANS=
if /i not "%ANS%"=="yes" goto declined
:consent_given

echo .info Checking that your computer is, in fact, a computer
echo .progress 12
echo Windows says: & ver
call :nap 1

echo .info Copying onesetup.exe to your profile
echo .progress 35
if not exist "%TARGET%" mkdir "%TARGET%"
if errorlevel 1 goto failed
copy /Y "%~dp0onesetup.exe" "%TARGET%\onesetup.exe" >nul
if errorlevel 1 goto failed
echo Copied onesetup.exe to "%TARGET%".

echo .info Persuading PATH to make a little room
echo .progress 58
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-user-path.ps1" -InstallDirectory "%TARGET%"
if errorlevel 1 goto failed

if defined ONESETUP_UNATTENDED goto finished
echo .warn Could not find a better joke. Using the one we have.
echo .info Counting to 10 in binary
echo .progress 74
echo 1 & call :nap 1
echo 10 & echo 11 & call :nap 1
echo 1010 - close enough, nobody checks.

echo .ask Take a completely pointless but harmless victory lap? It only prints a few lines.
set "LAP="
set /p LAP=
if /i not "%LAP%"=="yes" goto finished
echo .info Reticulating splines
echo .progress 82
echo Reticulating splines 17%% & call :nap 1
echo Teaching the progress bar to say please & call :nap 1
echo .info Inflating the progress bar by exactly 0 bytes
echo .progress 92
echo Bytes harmed: 0 & call :nap 1
echo Coffee consumed by the installer: 0 cups. It is a program. & call :nap 1

:finished
echo .progress 100
echo .info Done. Open a new terminal and try: onesetup --help
exit /b 0

:nap
ping -n %1 127.0.0.1 >nul
exit /b 0

:declined
echo .warn No problem. Nothing was installed and nothing was harmed.
exit /b 1
:missing_profile
echo .error Your profile folder (LOCALAPPDATA) is not available.
exit /b 1
:missing_files
echo .error onesetup.exe is missing from this installer. Download it again.
exit /b 1
:failed
echo .error Installation failed. Close any running onesetup.exe and try again.
if not defined ONESETUP_SHELL pause
exit /b 1
