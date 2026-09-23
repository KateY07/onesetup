& {
    $ErrorActionPreference = "Stop"
    $downloadUrl = "https://github.com/KateY07/onesetup/releases/download/v3.pre3/onesetup.exe"
    $outputPath = Join-Path -Path (Get-Location).Path -ChildPath "onesetup.exe"

    if (Test-Path -LiteralPath $outputPath) {
        throw "File already exists: $outputPath"
    }

    Invoke-WebRequest -Uri $downloadUrl -OutFile $outputPath -UseBasicParsing
    Write-Host "Downloaded: $outputPath"
}
