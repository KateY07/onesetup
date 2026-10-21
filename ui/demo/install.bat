@echo off
rem OneSetup UI demo. Lines starting with "." control the UI; everything else is shown as debug output.
rem Keep this file ASCII: cmd can cut a UTF-8 line that crosses its 512-byte read block.
rem Localised text lives in msg-*.txt and is printed with "type" (type is not affected).
echo .title Demo Application
type msg-check.txt
echo .progress 5
ver
ping -n 2 127.0.0.1 >nul

type msg-ask.txt
set /p ANS=
if /i not "%ANS%"=="yes" (
    type msg-declined.txt
    exit /b 1
)

echo .choose Which flavour of demo do you want?; Standard; Extra crunchy; Minimal
set "PICK="
set /p PICK=
echo You picked option %PICK%.

type msg-copy.txt
for %%P in (20 40 60) do (
    echo .progress %%P
    echo copying part %%P ...
    ping -n 2 127.0.0.1 >nul
)

type msg-warn.txt
echo .progress 85
echo .\relative-looking line is plain output, not a command
echo something on stderr 1>&2
ping -n 2 127.0.0.1 >nul

echo .progress 100
type msg-done.txt
exit /b 0
