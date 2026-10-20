# OneSetup

把一个目录打包成单文件安装器。运行生成的 `.exe` 时，会自动解压到临时目录，并执行目录中的 `install.bat`（如果存在）。

```powershell
.\onesetup.exe <目录路径> [-o <输出文件>] [-l <0-9>]
```

不指定 `-o` 时，输出文件默认为 `<目录名>_setup.exe`。

示例：

```powershell
.\onesetup.exe app                          # 把 app 目录打包为 app_setup.exe
.\onesetup.exe app -o dist\setup.exe -l 5    # 指定输出文件和压缩等级
.\onesetup.exe app --ui -o setup.exe        # 带进度界面的安装包（需要 app\install.bat）
.\onesetup.exe --help                       # 完整用法、安装包行为、界面协议摘要
```

生成的安装包运行时会解压到临时目录并执行其中的 `install.bat`，结束后删除该目录；`setup.exe -y` 跳过确认提示。`--ui-exe <文件>` 与 `--ui` 相同，但使用自定义的 `onesetup-ui.exe`。

`-l/--level` 为压缩等级：`0` 仅存储（最快，适合已压缩内容），`9` 最小体积（默认，最慢）。打包时显示百分比进度：终端中为一行刷新，重定向输出时每 10% 一行。生成的安装包运行时自带解压进度条。

内置的 7zSD 安装模块只识别 `Title`、`BeginPrompt`、`Progress`、`RunProgram`、`Directory`、`ExecuteFile`、`ExecuteParameters` 七个配置项；其他键（如 `OverwriteMode`、`TempMode`）会被忽略，因此不再写入。

运行自动化集成测试：

```powershell
dotnet run --project .\tests\OneSetup.IntegrationTests -- .\publish\onesetup.exe
```

## 界面外壳（`--ui`）

`--ui` 让安装包通过一个内置的小界面运行 `install.bat`：步骤清单、进度条、一行滚动日志、询问抽屉和完成动画。`install.bat` 通过输出以 `.` 开头的指令控制界面，协议见 [ui/README.md](ui/README.md)。

界面外壳的源码在 `ui/native`（C++，Win32 + Direct2D）。构建 `onesetup` 时会自动用 MSVC 编译并内嵌，因此需要安装 Visual Studio 的“使用 C++ 的桌面开发”工作负载。没有 C++ 工具链时，可以传入预先编译好的外壳：

```powershell
dotnet publish -c Release -p:OneSetupUiExe=D:\path\to\onesetup-ui.exe
```

