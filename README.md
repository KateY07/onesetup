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

不指定 `-o` 时，输出文件默认为 `<目录名>_setup.exe`。

运行自动化集成测试：

```powershell
dotnet run --project .\tests\OneSetup.IntegrationTests -- .\publish\onesetup.exe
```
