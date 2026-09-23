# OneSetup

把一个目录打包成单文件安装器。运行生成的 `.exe` 时，会自动解压到临时目录，并执行目录中的 `install.bat`（如果存在）。

推荐直接使用 [Release 页面](https://github.com/KateY07/onesetup/releases) 中已编译好的版本。PowerShell 一行下载命令：

```powershell
irm "https://raw.githubusercontent.com/KateY07/onesetup/main/install.ps1" | iex
```

重复执行该命令会覆盖当前目录的 `onesetup.exe`，可用于更新或重新安装。

```powershell
.\onesetup.exe <目录路径> [-o <输出文件>]
```

不指定 `-o` 时，输出文件默认为 `<目录名>_setup.exe`。`onesetup.exe` 需要 .NET 10 Runtime，7z 打包组件和 SFX 解压模块已内置；生成的安装器运行时不依赖外部 .NET 或 7-Zip。`install.bat` 自身调用的程序依赖仍需由安装目标机提供。
