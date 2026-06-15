# 从命令行参数获取版本号,如果未提供则使用默认值
param(
    [string]$Version = "2.0.0"
)

$ErrorActionPreference = "Stop"

function Log([string]$msg, [string]$color = "Yellow") {
    Write-Host $msg -ForegroundColor $color
}

# 去除版本号中的 'v' 前缀(如果存在)
$CleanVersion = $Version -replace '^v', ''
Log "开始构建 STranslate 版本: $CleanVersion" "Green"

# 更新./src/SolutionAssemblyInfo.cs 中的版本号
$asmInfo = "./src/SolutionAssemblyInfo.cs"

if (Test-Path $asmInfo) {
    # 读取文件内容
    $content = Get-Content $asmInfo -Raw

    # 需要替换的行 —— 直接按正则匹配三种属性
    $patterns = @{
        'AssemblyVersion'              = 'AssemblyVersion\("[^"]+"\)'
        'AssemblyFileVersion'          = 'AssemblyFileVersion\("[^"]+"\)'
        'AssemblyInformationalVersion' = 'AssemblyInformationalVersion\("[^"]+"\)'
    }

    foreach ($key in $patterns.Keys) {
        $pattern = $patterns[$key]
        $replacement = "$key(`"$CleanVersion`")"
        $content = [regex]::Replace($content, $pattern, $replacement)
    }

    # 写回文件
    Set-Content $asmInfo $content -Encoding UTF8

    Log "SolutionAssemblyInfo.cs 已更新为版本: $CleanVersion" "Green"
}
else {
    Log "未找到 $asmInfo，无法执行版本写入。" "Red"
    exit 1
}

# 清理构建输出
Log "正在清理之前的构建..."
$artifactPath = ".\src\.artifacts\Release\"

if (Test-Path $artifactPath) {
    Remove-Item -Path $artifactPath -Recurse -Force -ErrorAction SilentlyContinue
}

# 更新 Fody 配置文件
Log "正在更新 FodyWeavers..."

$src = "./src/STranslate/FodyWeavers.Release.xml"
$bak = "./src/STranslate/FodyWeavers.xml.bak"
$dst = "./src/STranslate/FodyWeavers.xml"

if (Test-Path $src) {
    Copy-Item $src $bak -Force
    Move-Item -Path $bak -Destination $dst -Force
} else {
    Log "未找到 $src，跳过更新。" "Red"
}

# 构建解决方案
Log "正在重新生成解决方案..."
dotnet build .\src\STranslate.slnx `
  --configuration Release `
  --no-incremental `
  /p:Version=$CleanVersion `
  /p:PackageVersion=$CleanVersion `
  /p:InformationalVersion=$CleanVersion


# 还原 FodyWeavers.xml
Log "正在还原 FodyWeavers.xml SolutionAssemblyInfo.cs..."
git restore $dst $asmInfo

# 清理插件目录中多余文件
Log "正在清理多余的 STranslate.Plugin 文件..."

$pluginsPath = "./src/.artifacts/Release/Plugins"
if (Test-Path $pluginsPath) {
    Get-ChildItem -Path $pluginsPath -Recurse -Include "STranslate.Plugin.dll","STranslate.Plugin.xml" |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

Log "构建完成！" "Green"
