# onesetup-ui

A ~200 KB native shell (Win32 + DirectComposition + Direct2D, no runtime to install) that runs `install.bat` and turns
its output into a small installer window. Pack with `onesetup.exe <dir> --ui`; build it with
`ui\native\build.cmd [output.exe]` (MSVC); building `onesetup` runs it and embeds the result. Frosted glass uses Acrylic blur-behind with our own white tint (`AcrylicTint` / `GlassTint` in `main.cpp`); if it is
unavailable the window falls back to a flat white card. Fixed 520x392 DIP window, DPI-aware.

## Protocol (stdout of install.bat)

| Line | Effect |
|---|---|
| `.title text` | window heading |
| `.info text` | new step in the checklist (previous step is ticked) |
| `.warn text` / `.error text` | amber / red row |
| `.progress 79` or `79%` | progress bar |
| `.ask text` | Yes/No sheet; the answer `yes` or `no` is written to stdin: `set /p ANS=` |
| anything else | plain output, kept in the collapsible Details log (stderr too) |

Keep `install.bat` ASCII: `cmd` can cut a UTF-8 line that crosses its 512-byte read block. Put localised text in a UTF-8
file and print it with `type msg.txt` (the file may itself contain `.info ...` lines). See `ui/demo`.

The window shows the newest output line as a one-line rolling log under the progress bar; `Details` opens the full log.
On success the badge turns green with a drawn check, a ripple and confetti; failure turns it red.

`install.bat` runs with `ONESETUP_SHELL=ui` (or `headless`) and with stdin connected to the shell, so do not use `pause`
or `choice` unguarded: `if not defined ONESETUP_SHELL pause`.

Programs the script starts (`start "" "%TARGET%pp.exe"`) keep running after the installer closes; the shell does not
wait for them or stop them. Only Cancel stops the script and everything it started so far.

The generated installer always exits 0 (the 7zSD module does not pass the script's exit code on); the shell itself
returns the script's exit code (2 = cancelled) but the SFX discards it. Automation should rely on a log or marker file
written by `install.bat`.

Exit code = install.bat exit code (2 = cancelled). Flags: `--headless` (no window, lines printed as `[kind] text`;
also `ONESETUP_UI=0`; `.ask` answers from `ONESETUP_ASK`, default yes), `--auto-close`, `--no-glass`.
