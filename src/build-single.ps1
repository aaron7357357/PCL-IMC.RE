# 一键构建「单文件版」PCL IMC.RE：
#   1) 编译 PCLPatch.dll
#   2) ILRepack 合并进 exe（输出文件名必须是 Plain Craft Launcher 2.exe，否则程序集名会变，WPF 资源加载失败）
#   3) SelfLink 把 Bootstrap.Install 的调用重新插回 PageLaunchRight.Init
#   4) 校验 + 部署到 D:\awa 和 dist
param(
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'
$ws = 'D:\Documents\deepseek-harness\default-workspace'
$patch = Join-Path $ws 'patch'
$build = Join-Path $ws 'build\patch'
$preset = Join-Path $ws 'build\preset'
$tmp = 'D:\awa\pcl-src\singlefile'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll",
    "System.dll", "System.Drawing.dll", "System.Web.Extensions.dll"
)

Write-Host '[1/4] 编译 PCLPatch.dll'
$cargs = @('/nologo', '/target:library', '/codepage:65001') +
    @("/resource:$patch\IMCHome.xaml,IMCHome.xaml",
      "/resource:$patch\TCHome.xaml,TCHome.xaml",
      "/resource:$patch\zan.jpg,zan.jpg",
      "/resource:$patch\banner.png,banner.png") +
    @("/out:$build\PCLPatch.dll") + ($refs | ForEach-Object { "/r:$_" }) +
    @("$patch\PCLPatch.cs", "$patch\ImcNative.cs", "$patch\TcNative.cs", "$patch\Egg.cs")
& $csc @cargs
if ($LASTEXITCODE -ne 0) { throw 'DLL 编译失败' }

Write-Host '[2/4] 合并进 exe'
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$merged = Join-Path $tmp 'Plain Craft Launcher 2.exe'
Remove-Item $merged -Force -ErrorAction SilentlyContinue
$tool = 'D:\awa\pcl-src\ilrepack\x\tools\ILRepack.exe'
& $tool "/out:$merged" /internalize "/lib:$patch" "/lib:D:\awa\pcl-src\run" "$preset\preset7.exe" "$build\PCLPatch.dll" | Out-Null
if (-not (Test-Path $merged)) { throw '合并失败' }

Write-Host '[3/4] 重新链接 Bootstrap.Install'
$linked = Join-Path $ws 'single-final.exe'
Remove-Item $linked -Force -ErrorAction SilentlyContinue
& (Join-Path $patch 'SelfLink.exe') $merged $linked | Out-Null
if (-not (Test-Path $linked)) { throw 'SelfLink 失败' }

$an = [System.Reflection.AssemblyName]::GetAssemblyName($linked)
Write-Host ("      程序集：{0} v{1}，{2} 字节" -f $an.Name, $an.Version, (Get-Item $linked).Length)

if (-not $NoDeploy) {
    Write-Host '[4/4] 部署'
    Get-Process 'Plain Craft Launcher 2' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    $backup = 'D:\awa\备份'
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    if (Test-Path 'D:\awa\Plain Craft Launcher 2.exe') {
        Copy-Item 'D:\awa\Plain Craft Launcher 2.exe' (Join-Path $backup ('Plain Craft Launcher 2.exe.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak')) -Force
    }
    Copy-Item $linked 'D:\awa\Plain Craft Launcher 2.exe' -Force
    Remove-Item 'D:\awa\PCLPatch.dll' -Force -ErrorAction SilentlyContinue

    $dist = 'D:\awa\pcl主页设计\imc二代主页设计\dist'
    $lite = Join-Path $dist '轻量版'
    $full = Join-Path $dist '完整版'
    Copy-Item $linked (Join-Path $lite 'Plain Craft Launcher 2.exe') -Force
    Remove-Item (Join-Path $lite 'PCLPatch.dll') -Force -ErrorAction SilentlyContinue
    Copy-Item $linked (Join-Path $full '改版PCL\Plain Craft Launcher 2.exe') -Force
    Remove-Item (Join-Path $full '改版PCL\PCLPatch.dll') -Force -ErrorAction SilentlyContinue
    foreach ($f in @('PCLPatch.cs', 'ImcNative.cs', 'TcNative.cs', 'Egg.cs', 'IMCHome.xaml', 'TCHome.xaml', 'zan.jpg', 'banner.png')) {
        Copy-Item (Join-Path $patch $f) (Join-Path $full 'src') -Force
    }
    Copy-Item (Join-Path $build 'PCLPatch.dll') (Join-Path $full 'src') -Force

    $z1 = Join-Path $dist 'PCL IMC.RE 轻量版.zip'
    $z2 = Join-Path $dist 'PCL IMC.RE 完整版.zip'
    Remove-Item $z1, $z2 -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path $lite -DestinationPath $z1 -CompressionLevel Optimal
    Compress-Archive -Path $full -DestinationPath $z2 -CompressionLevel Optimal

    $desk = [Environment]::GetFolderPath('Desktop')
    Copy-Item $linked (Join-Path $desk 'PCL IMC.RE v1.3 单文件.exe') -Force
    Copy-Item $z1 (Join-Path $desk 'PCL IMC.RE v1.3 单文件.zip') -Force

    Start-Process 'D:\awa\Plain Craft Launcher 2.exe'
    Write-Host ("      完成，MD5：{0}" -f (Get-FileHash $linked -Algorithm MD5).Hash)
}
