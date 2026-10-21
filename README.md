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
.\onesetup.exe --version                    # 显示版本（4.1）
```

生成的安装包运行时会解压到临时目录并执行其中的 `install.bat`，结束后删除该目录；`setup.exe -y` 跳过确认提示。`--ui-exe <文件>` 与 `--ui` 相同，但使用自定义的 `onesetup-ui.exe`。

`--icon <文件.ico>` 设置生成的 exe 的图标；不指定时使用 OneSetup 自己的图标（安装窗口在任务栏和标题栏也显示它）。ICO 可以带多个尺寸（16–256），`assets/make-icon.ps1` 是生成示例图标的脚本。

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

## 退出码

生成的安装包会返回 `install.bat` 的退出码（7zSD 模块本身恒返回 0，所以在它前面加了一个很小的外壳来收集真实退出码）。`--ui` 时在窗口里点“取消”返回 2；解压或启动失败返回非 0。`onesetup` 自身：0 成功，1 出错，2 缺少参数。

## 单文件应用（`--app`，可选）

`--app <程序>` 生成的不是安装器，而是“单文件直接启动”的应用：首次运行时解压到 `%LOCALAPPDATA%\OneSetup\apps\<内容哈希>`，之后每次运行只检查一个标记文件，然后直接从缓存启动 `<程序>`。

```powershell
.\onesetup.exe tool --app tool.exe -l 1 -o tool_portable.exe   # 首次解压更快：-l 1 或 0
.\tool_portable.exe --any args                                 # 参数、工作目录、控制台、退出码、Ctrl+C 都原样传递
```

- **第二次启动的额外耗时**约等于多启动一个进程（本机实测约 10 ms，对比直接运行同一个程序）；路径上没有解压、没有哈希计算、没有界面，只有一次文件状态检查和一次 `CreateProcess`。
- 控制台程序和脚本得到控制台版启动器（能正常读写标准输入输出），图形界面程序得到无控制台版，不会闪黑窗。
- 缓存目录以内容哈希命名：程序更新（内容变化）会生成新目录，旧目录不会自动删除；环境变量 `ONESETUP_CACHE` 可改缓存位置。
- **首次运行较慢**：首次解压目前走 7-Zip 自解压模块，很小的包也要约 2 秒（多为固定开销）；大包可用 `-l 1` 或 `-l 0` 加快。这是已知的取舍。
- 多个实例同时首次运行是安全的（先解压到临时目录，再原子改名，只留下一份）。
- `--app` 不运行 `install.bat`，也不能与 `--ui` 同时使用。缓存位于用户可写的位置，所以它不提供防篡改保护。

## 安装 OneSetup 本身

OneSetup 用自己打包了自己：从 [Releases](https://github.com/KateY07/onesetup/releases) 下载 `onesetup-setup.exe`，运行后会把 `onesetup.exe` 复制到 `%LOCALAPPDATA%\OneSetup` 并加入当前用户的 PATH（不需要管理员权限），然后提供一段无害的幽默演示（可以拒绝；设置环境变量 `ONESETUP_UNATTENDED=1` 可跳过提问和演示）。

自己构建安装包：

```powershell
.\installer\build-installer.ps1      # 生成 dist\onesetup-setup.exe 及其 SHA-256
```

