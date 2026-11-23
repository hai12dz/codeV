# Build bang csc.exe co san trong Windows (.NET Framework) - khong can cai gi.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "Khong tim thay csc.exe tai $csc" }

$bin = Join-Path $root 'bin'
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

Write-Host '==> BhopAssist.exe'
& $csc /nologo /optimize+ /platform:x64 /target:exe `
    /out:"$bin\BhopAssist.exe" "$root\src\BhopAssist.cs"
if ($LASTEXITCODE -ne 0) { throw "BhopAssist build that bai" }

Write-Host '==> BhopTestbed.exe'
& $csc /nologo /optimize+ /platform:x64 /target:winexe `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /out:"$bin\BhopTestbed.exe" "$root\src\Testbed.cs"
if ($LASTEXITCODE -ne 0) { throw "BhopTestbed build that bai" }

Write-Host ''
Write-Host "Xong. File nam trong: $bin"
Get-ChildItem $bin -Filter *.exe | Format-Table Name, Length, LastWriteTime -AutoSize
