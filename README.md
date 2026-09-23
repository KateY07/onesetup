# OneSetup

把一个目录打包成单文件安装器。运行生成的 `.exe` 时，会自动解压到临时目录，并执行目录中的 `install.bat`（如果存在）。

推荐直接使用 [Release 页面](https://github.com/KateY07/onesetup/releases) 中已编译好的版本。PowerShell 一行下载命令：

```powershell
Invoke-WebRequest "https://github.com/KateY07/onesetup/releases/download/v3.pre3/onesetup.exe" -OutFile ".\onesetup.exe"
```

```powershell
.\onesetup.exe <目录路径> [-o <输出文件>]
```

不指定 `-o` 时，输出文件默认为 `<目录名>_setup.exe`。`onesetup.exe` 需要 .NET 10 Runtime 和打包机上的 `7z.exe`；生成的文件是 7z 自解压格式，目标电脑无需安装 .NET 或 7-Zip。
