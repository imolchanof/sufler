$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
Push-Location $PSScriptRoot
try {
    & "$framework\csc.exe" /nologo /target:winexe /out:MakeIcon.exe /reference:System.Drawing.dll MakeIcon.cs
    if ($LASTEXITCODE -ne 0) { throw 'Icon compilation failed' }
    $iconProcess = Start-Process -FilePath "$PSScriptRoot\MakeIcon.exe" -WindowStyle Hidden -PassThru -Wait
    if ($iconProcess.ExitCode -ne 0) { throw 'Icon generation failed' }
    New-Item -ItemType Directory -Path "$PSScriptRoot\build" -Force | Out-Null
    & "$framework\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /out:build\Sufler.exe /win32icon:Sufler.ico /resource:Sufler.ico,Sufler.ico /resource:Fonts\Inter-Regular.ttf,Inter-Regular.ttf /resource:Fonts\Inter-SemiBold.ttf,Inter-SemiBold.ttf /resource:Fonts\Inter-LICENSE.txt,Inter-LICENSE.txt /resource:Settings.xaml,Settings.xaml /resource:Main.xaml,Main.xaml /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Xaml.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$wpf\WindowsBase.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\PresentationFramework.dll" Sufler.cs
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
} finally { Pop-Location }
