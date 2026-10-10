<#
编译门（不依赖 Unity，只依赖本机 dotnet + Unity 安装目录里的 DLL）。

为什么需要它：
  *.csproj 只由 Unity 在编辑器里重新生成。改代码时一旦新增/删除/改名文件，仓库里那两份
  csproj（Assembly-CSharp / Assembly-CSharp-Editor）的 <Compile Include> 清单就是过期的 ——
  直接 build 它会漏编译新文件，或因为清单里的旧文件不存在而报错。
  本脚本因此**现场 glob** 出临时工程（落在 Temp/compile-gate/，被 .gitignore 覆盖；
  不进 Unity、不进构建产物）。

两条编译路径（2026-10 起：Assets/Scripts 有了 asmdef 边界）：

  ① asmdef 感知路径（本脚本的主要职责）
     每个 .asmdef 生成一个临时工程，按 asmdef 的 references 真实串 ProjectReference，
     源码 = 该 asmdef 目录下的 .cs（**扣除**更深一层 asmdef 的目录 —— 与 Unity 的
     "最近祖先 asmdef 胜出" 同规则）。
     这样在没有 Unity 的情况下也能强制：层边界、缺引用、**循环依赖**（MSBuild 会直接报
     「cycle detected」）。这是把「命名空间约定」升级成「编译器约束」后唯一还能验的东西。

  ② 预定义程序集残留路径
     Assets/Scripts 与 Assets/Tests 下**没有被任何 asmdef 覆盖**的 .cs 仍落
     Assembly-CSharp / Assembly-CSharp-Editor（Unity 的预定义程序集规则）。
     这类文件单独生成 Gate.csproj / GateEditor.csproj，并对所有 autoReferenced 的
     asmdef 工程加 ProjectReference（预定义程序集自动引用它们）。残留为空则不生成该工程
     （没有源文件的 csproj 会报 CS2008）。

拓扑守卫（-NoTopologyGuard 可跳过）：
  ① 目录 → 应有 asmdef 名。防的是「asmdef 被删/改名后，源码静默退回 Assembly-CSharp，
     边界消失且不报错」。
  ② asmdef 反向引用黑名单。防的是「有人给 Foundation 加上 Data 引用」这类逆层依赖 ——
     MSBuild 只能报环，报不出单向的越界。
  ③ asmdef 重名检测。撞名会让**整个工程所有程序集**都编译不出来
     （见 ConfigWorkspace/AGENTS.md 里 Luban.Runtime 的实测坑）。

用法：
  pwsh Tools/compile-gate.ps1              # 生成 + 编译
  pwsh Tools/compile-gate.ps1 -NoBuild     # 只生成工程
  pwsh Tools/compile-gate.ps1 -Topology    # 打印程序集拓扑表

退出码：0 = 0 error；1 = 有 error 或前置检查失败。
基线（2026-10-05，批 0）：0 error / 1 warning（SaveService.cs CS0649，既有）。
警告计数口径（2026-10-07 修正）：数的是**去重后的逐条诊断**（`路径(行,列): warning CSxxxx: 消息`）。
  两个坑各踩过一次：① 汇总行 `    1 个警告` 的缩进里含 `: `，被旧写法算成了诊断；
  ② `dotnet build` 把同一条诊断打印两遍（编译阶段一次、末尾汇总段一次）。
  症状都是"代码没动、门的数字变了"，而那种数字不可用于验收。见脚本末尾的计数函数。
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoTopologyGuard,
    [switch]$Topology
)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'Temp/compile-gate'
$asmOut = Join-Path $outDir 'asmdef'
$utf8   = [System.Text.UTF8Encoding]::new($false)

$runtimeTemplate = Join-Path $root 'Assembly-CSharp.csproj'
$editorTemplate  = Join-Path $root 'Assembly-CSharp-Editor.csproj'

if (-not (Test-Path $asmOut)) { New-Item -ItemType Directory -Path $asmOut -Force | Out-Null }

# ---------------------------------------------------------------
# 生成模板：任取一份 Unity 生成的 csproj 即可（它们的 PropertyGroup 结构一致，
# 只差工程名与源码清单，而源码清单一律由本脚本现场重算）。
#
# 🔴 为什么不硬要求那两份预定义工程：asmdef 一落地，预定义程序集可能只剩一两个文件，
# 甚至一个都不剩 —— 那时 Unity 就不再生成 Assembly-CSharp(-Editor).csproj，
# 硬要求它们会让整个门起不来（实测风险点：Tests/Runtime/Editor 归了测试 asmdef 之后，
# Assembly-CSharp-Editor 就没有源文件了）。
# ---------------------------------------------------------------
$templateCandidates = @($runtimeTemplate, $editorTemplate) + @(
    (Get-ChildItem -LiteralPath $root -Filter *.csproj -File | ForEach-Object { $_.FullName } | Sort-Object)
)

$genTemplate = $null
foreach ($c in $templateCandidates) {
    if ([string]::IsNullOrEmpty($c) -or -not (Test-Path $c)) { continue }
    if ((Get-Content -Raw -LiteralPath $c) -match '<Import Project="Sdk\.props" Sdk="Microsoft\.NET\.Sdk" />') { $genTemplate = $c; break }
}

if (-not $genTemplate) {
    throw '仓库根下找不到任何 Unity 生成的 csproj（需要一份带 Sdk.props 导入的模板）—— 先让 Unity 重新生成 *.csproj'
}

Write-Host ("生成模板：{0}" -f [System.IO.Path]::GetFileName($genTemplate)) -ForegroundColor DarkGray

# ======================================================================
# 1 · 参照池：从两份真实 csproj 里刮出「程序集名 → 绝对 HintPath」
#     用来把参照分成三类：
#       · BCL   —— Data\NetStandard\ / Data\UnityReferenceAssemblies\，**每个**程序集都要
#       · 引擎  —— 其余落在 Unity 安装目录下的（UnityEngine.*Module / UnityEditor.*）
#       · 包    —— 落在 Library\ 下的（Unity.InputSystem / Unity.TextMeshPro / UnityEngine.UI …）
#     后两类必须分开：UnityEngine.UI 名字像引擎，其实是包程序集，
#     只有 asmdef 显式引用才拿得到 —— 混在一起就把层边界放水了。
# ======================================================================

$pool = @{}

# 参照池从仓库根下**全部** csproj 里刮，不只那两份预定义工程。
# 为什么：Unity 一重新生成就会给每个 asmdef 产出自己的 csproj，包引用随之分散到各自文件里
# （Assembly-CSharp.csproj 此后不会再包含只被某一层用到的 Unity.TextMeshPro / UnityEngine.UI 之类）。
# 只读两份预定义工程的话，重新生成之后参照池会缺条目，门会误报"引用解析不到"。
# 只取 <Reference> ＋ <HintPath>，<ProjectReference> 一律忽略（那是仓库内 asmdef，另行处理）。
$poolSources = @(Get-ChildItem -LiteralPath $root -Filter *.csproj -File | ForEach-Object { $_.FullName } | Sort-Object)

foreach ($t in $poolSources) {
    $text = Get-Content -Raw -LiteralPath $t

    foreach ($m in [regex]::Matches($text, '(?s)<Reference Include="([^"]+)">\s*<HintPath>([^<]+)</HintPath>')) {
        $name = $m.Groups[1].Value
        if ($pool.ContainsKey($name)) { continue }

        $hint = $m.Groups[2].Value
        if (-not [System.IO.Path]::IsPathRooted($hint)) { $hint = Join-Path $root $hint }

        $pool[$name] = $hint
    }
}

$probe = @($pool.Keys | Where-Object { ($pool[$_] -replace '/', '\') -match '\\Data\\Managed\\UnityEngine\\' } | Select-Object -First 1)
if ($probe.Count -eq 0) { throw '参照池里没刮到任何引擎模块 —— 模板结构变了？' }

# Unity 安装前缀（……\Editor\Data\）：落在这下面的都是 Unity 自带的，
# 落在 Library\ 下面的才是包程序集（只认 asmdef 的显式引用）。
$null = ($pool[$probe[0]] -replace '/', '\') -match '^(.*?\\Editor\\Data\\)'
$unityDataPrefix = $Matches[1]

# 🔴 整份 BCL 必须无条件带上：模板里 NoStdLib=true ＋ DisableImplicitFrameworkReferences=true，
# System.Object / System.String 这些全靠 Data\NetStandard\ 与 UnityReferenceAssemblies\ 的显式清单提供。
# （漏了它们会得到满屏 CS0518「预定义类型 System.Object 未定义」，而病根在参照池而不在代码。）
$bclRefs    = @($pool.Keys | Where-Object { ($pool[$_] -replace '/', '\') -match '\\Editor\\Data\\(NetStandard|UnityReferenceAssemblies)\\' } | Sort-Object)
$engineRefs = @($pool.Keys | Where-Object {
        $h = $pool[$_] -replace '/', '\'
        $h.StartsWith($unityDataPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and $bclRefs -notcontains $_
    } | Sort-Object)

if ($bclRefs.Count -eq 0) { throw '参照池里没刮到 BCL 参照 —— 模板结构变了？' }

# 预编译托管插件（PackageCache 下的裸 DLL）：overrideReferences=false 的 asmdef 会自动引用它们。
# 典型例子：DeepseaOil.EditorTools.Tests 的 asmdef 里 precompiledReferences 是空的，
# NUnit 完全靠这条自动引用规则拿到 —— 只在 asmdef 图里找是找不到的。
# 这是近似：Unity 还按插件的 Auto Reference / 平台开关逐条过滤，这里一律带上。
$autoPluginRefs = @($pool.Keys | Where-Object { ($pool[$_] -replace '/', '\') -match '\\Library\\PackageCache\\' } | Sort-Object)

# ======================================================================
# 2 · 拓扑守卫表
# ======================================================================

# 目录 → 应有的 asmdef 名。少一个 / 改个名，源码就静默退回 Assembly-CSharp。
$expectedAsmdefs = [ordered]@{
    'Assets\Scripts\Foundation'      = 'DeepseaOil.Foundation'
    'Assets\Scripts\Data'            = 'DeepseaOil.Data'
    'Assets\Scripts\Logic'           = 'DeepseaOil.Logic'
    'Assets\Scripts\Presentation'    = 'DeepseaOil.Presentation'
    'Assets\Scripts\Generated'       = 'cfg'
    'Assets\Scripts\Generated\Input' = 'DeepseaOil.Generated.Input'
    'Assets\Editor'                  = 'DeepseaOil.EditorTools'
    'Assets\Tests\Runtime\Editor'    = 'DeepseaOil.Tests.EditMode'
    'Assets\Tests\Scene'             = 'DeepseaOil.Tests.Scene'
    'Assets\Tests\Tools'             = 'DeepseaOil.EditorTools.Tests'
}

# 反向依赖黑名单：层级只能单向。MSBuild 只报「环」，报不出单向越界。
# 允许的方向：cfg ← Data ← Logic ← Presentation，Foundation 是最底层（谁都能引它）。
$forbiddenRefs = @{
    'DeepseaOil.Foundation'      = @('DeepseaOil.Data', 'DeepseaOil.Logic', 'DeepseaOil.Presentation', 'cfg', 'DeepseaOil.Generated.Input', 'DeepseaOil.EditorTools')
    'cfg'                        = @('DeepseaOil.Foundation', 'DeepseaOil.Data', 'DeepseaOil.Logic', 'DeepseaOil.Presentation', 'DeepseaOil.Generated.Input', 'DeepseaOil.EditorTools')
    'DeepseaOil.Generated.Input' = @('DeepseaOil.Foundation', 'DeepseaOil.Data', 'DeepseaOil.Logic', 'DeepseaOil.Presentation', 'cfg', 'DeepseaOil.EditorTools')
    'DeepseaOil.Data'            = @('DeepseaOil.Logic', 'DeepseaOil.Presentation', 'DeepseaOil.Generated.Input', 'DeepseaOil.EditorTools')
    'DeepseaOil.Logic'           = @('DeepseaOil.Presentation')
}

# ======================================================================
# 3 · 解析所有 asmdef
# ======================================================================

$defs = @()

foreach ($f in (Get-ChildItem (Join-Path $root 'Assets') -Recurse -File -Filter *.asmdef | Sort-Object FullName)) {
    $json = Get-Content -Raw -LiteralPath $f.FullName | ConvertFrom-Json

    if (-not $json.name) { throw "asmdef 缺少 name 字段：$($f.FullName)" }

    $defs += [pscustomobject]@{
        Name       = [string]$json.name
        Dir        = $f.DirectoryName
        File       = $f.FullName
        Refs       = @($json.references           | Where-Object { $_ })
        Precomp    = @($json.precompiledReferences | Where-Object { $_ })
        Override   = [bool]$json.overrideReferences
        NoEngine   = [bool]$json.noEngineReferences
        Unsafe     = [bool]$json.allowUnsafeCode
        EditorOnly = (@($json.includePlatforms) -contains 'Editor')
        AutoRef    = [bool]$json.autoReferenced
    }
}

# ③ 重名检测（撞名 = 整个工程编译不出来，且报错点离现场很远）
$dupes = @($defs | Group-Object Name | Where-Object { $_.Count -gt 1 })
if ($dupes.Count -gt 0) {
    $msg = ($dupes | ForEach-Object { "  {0}`n    {1}" -f $_.Name, (($_.Group | ForEach-Object { $_.File }) -join "`n    ") }) -join "`n"
    throw "asmdef 重名 —— 撞名会导致整个工程所有程序集都编译不出来：`n$msg"
}

$byName = @{}
foreach ($d in $defs) { $byName[$d.Name] = $d }

function Get-AsmdefGuid {
    param([string]$AsmdefPath)
    $meta = $AsmdefPath + '.meta'
    if (-not (Test-Path $meta)) { return $null }
    $m = [regex]::Match((Get-Content -Raw -LiteralPath $meta), '(?m)^guid:\s*([0-9a-fA-F]+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

# ======================================================================
# 4 · 源码归属：最近祖先 asmdef 胜出
# ======================================================================

$owner = @{}   # 绝对路径 → asmdef 名

foreach ($d in $defs) {
    $nested = @($defs | Where-Object { $_.Dir -ne $d.Dir -and $_.Dir.StartsWith($d.Dir + '\') })

    foreach ($f in (Get-ChildItem $d.Dir -Recurse -File -Filter *.cs)) {
        $shadowed = $false
        foreach ($n in $nested) {
            if ($f.FullName.StartsWith($n.Dir + '\')) { $shadowed = $true; break }
        }
        if (-not $shadowed) { $owner[$f.FullName] = $d.Name }
    }
}

# ======================================================================
# 5 · 生成工程
# ======================================================================

function Write-GateProject {
    param(
        [string]$OutPath,
        [string]$AssemblyName,
        [string[]]$CompileFiles,
        [string[]]$AssemblyRefs,
        [string[]]$ProjectRefs,
        [switch]$EditorOnly,
        [switch]$AllowUnsafe
    )

    $text = Get-Content -Raw -LiteralPath $genTemplate

    # 去掉模板里的显式清单，改由现场算出来的文件表覆盖
    $text = [regex]::Replace($text, '(?m)^\s*<Compile Include="[^"]+" />\r?\n', '')
    $text = [regex]::Replace($text, '(?s)\s*<Reference Include="[^"]+">.*?</Reference>', '')
    $text = [regex]::Replace($text, '(?m)^\s*<Reference Include="[^"]+"\s*/>\r?\n', '')
    $text = [regex]::Replace($text, '(?m)^\s*<ProjectReference Include="[^"]+" />\r?\n', '')

    $text = [regex]::Replace($text, '<AssemblyName>[^<]*</AssemblyName>', "<AssemblyName>$AssemblyName</AssemblyName>")

    if ($EditorOnly -and $text -notmatch 'UNITY_EDITOR_ONLY_COMPILATION') {
        $text = [regex]::Replace($text, '(<DefineConstants>[^<]*)</DefineConstants>', '$1;UNITY_EDITOR_ONLY_COMPILATION</DefineConstants>')
    }

    # asmdef 的 allowUnsafeCode（模板默认 False；Luban.Runtime 开了，漏掉就是满屏 CS0227）
    if ($AllowUnsafe) {
        $text = [regex]::Replace($text, '(?i)<AllowUnsafeBlocks>false</AllowUnsafeBlocks>', '<AllowUnsafeBlocks>True</AllowUnsafeBlocks>')
    }

    $compileXml = ($CompileFiles | ForEach-Object { '    <Compile Include="' + $_ + '" />' }) -join "`n"

    $refXml = ($AssemblyRefs | Sort-Object -Unique | ForEach-Object {
            '    <Reference Include="' + $_ + '">' + "`n" +
            '      <HintPath>' + $pool[$_] + '</HintPath>' + "`n" +
            '      <Private>False</Private>' + "`n" +
            '    </Reference>'
        }) -join "`n"

    $projXml = ($ProjectRefs | Sort-Object -Unique | ForEach-Object { '    <ProjectReference Include="' + $_ + '" />' }) -join "`n"

    # 🔴 DisableTransitiveProjectReferences 是这份门最关键的一个开关。
    # MSBuild 的 ProjectReference **默认传递**（A→B→C 时 A 也拿得到 C 的类型），
    # 而 Unity 的 asmdef references **不传递**。不关掉它，门会比 Unity 宽松：
    # 少写一条 references 也照样绿，直到有人在 Unity 里打开才炸
    # （实测：DeepseaOil.Tests.Scene 少写 DeepseaOil.Foundation，
    #  GameRoot 的基类 Singleton<> 解析不到 → CS0012 ＋ CS0117）。
    $transitive = "  <PropertyGroup>`n" +
                  "    <DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences>`n" +
                  "  </PropertyGroup>`n"

    $block = $transitive +
             "  <ItemGroup>`n$compileXml`n  </ItemGroup>`n" +
             "  <ItemGroup>`n$refXml`n  </ItemGroup>`n" +
             "  <ItemGroup>`n$projXml`n  </ItemGroup>`n"

    $anchor = '<Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />'
    if ($text -notmatch [regex]::Escape($anchor)) { throw "工程模板结构变了（找不到 Sdk.targets 导入）：$genTemplate" }

    $text = $text.Replace($anchor, $block + $anchor)

    [System.IO.File]::WriteAllText($OutPath, $text, $utf8)
}

# ---- 5a · 每个 asmdef 一个工程 ----

$gateOf    = @{}     # asmdef 名 → 临时工程绝对路径
$missing   = @()     # 解析不出来的引用
$violations = @()    # 拓扑守卫命中的越界
$generated = @()     # 待编译的工程绝对路径

foreach ($d in $defs) {
    $safe    = [regex]::Replace($d.Name, '[^A-Za-z0-9_.]', '_')
    $gateOf[$d.Name] = Join-Path $asmOut ("gate_{0}.csproj" -f $safe)
}

foreach ($d in $defs) {
    $compile = @($owner.Keys | Where-Object { $owner[$_] -eq $d.Name } | Sort-Object)

    $asmRefs = @()
    $projRefs = @()

    $asmRefs += $bclRefs
    if (-not $d.NoEngine) { $asmRefs += $engineRefs }
    if (-not $d.Override)   { $asmRefs += $autoPluginRefs }

    foreach ($r in $d.Refs) {
        $name = $r

        # GUID 形式的引用（Unity 会把引用改写成 GUID:xxx）
        if ($name.StartsWith('GUID:')) {
            $guid = $name.Substring(5)
            $hit = @($defs | Where-Object { (Get-AsmdefGuid $_.File) -eq $guid })
            if ($hit.Count -eq 1) { $name = $hit[0].Name }
            else { $missing += "$($d.Name) → $r（GUID 解析不到仓库内 asmdef）"; continue }
        }

        if ($byName.ContainsKey($name)) {
            $projRefs += $gateOf[$name]

            if ($forbiddenRefs.ContainsKey($d.Name) -and $forbiddenRefs[$d.Name] -contains $name) {
                $violations += "$($d.Name) 引用了 $name（反向依赖：$($d.File)）"
            }

            continue
        }

        if ($pool.ContainsKey($name)) { $asmRefs += $name; continue }

        $missing += "$($d.Name) → $name（参照池里没有这个程序集）"
    }

    # precompiledReferences：nunit.framework.dll 这类不在 asmdef 图里的 DLL
    foreach ($p in $d.Precomp) {
        $pn = [System.IO.Path]::GetFileNameWithoutExtension($p)

        if ($pool.ContainsKey($pn)) { $asmRefs += $pn; continue }

        $found = Get-ChildItem (Join-Path $root 'Library/PackageCache') -Recurse -File -Filter $p -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $pool[$pn] = $found.FullName
            $asmRefs += $pn
            continue
        }

        $missing += "$($d.Name) → $p（PackageCache 里找不到）"
    }

    Write-GateProject `
        -OutPath        $gateOf[$d.Name] `
        -AssemblyName   ("Gate_" + [regex]::Replace($d.Name, '[^A-Za-z0-9_.]', '_')) `
        -CompileFiles   $compile `
        -AssemblyRefs   $asmRefs `
        -ProjectRefs    $projRefs `
        -EditorOnly:$d.EditorOnly `
        -AllowUnsafe:$d.Unsafe

    $generated += $gateOf[$d.Name]

    Write-Host ("  {0,-32} {1,4} 个 .cs，引用 {2}" -f $d.Name, $compile.Count, (($d.Refs | Sort-Object) -join ', '))
}

# ---- 5b · 预定义程序集的残留源码 ----

$autoRefGates = @($defs | Where-Object { $_.AutoRef } | ForEach-Object { $gateOf[$_.Name] })

function New-LeftoverProject {
    param(
        [string]$OutPath,
        [string]$AssemblyName,
        [string[]]$Files,
        [string[]]$ExtraProjectRefs
    )

    if ($Files.Count -eq 0) { return 0 }

    # 预定义程序集看得见**全部**包程序集（旧脚本直接照抄 Assembly-CSharp.csproj 的参照清单，
    # 这里用整池等价表达）；同时对 autoReferenced 的 asmdef 工程加 ProjectReference。
    Write-GateProject `
        -OutPath      $OutPath `
        -AssemblyName $AssemblyName `
        -CompileFiles $Files `
        -AssemblyRefs @($pool.Keys) `
        -ProjectRefs  (@($autoRefGates) + @($ExtraProjectRefs))

    return $Files.Count
}

# 预定义程序集 = Assets 下**所有**没被 asmdef 覆盖的 .cs —— Unity 的规则就是这条，不是枚举目录。
# 路径里含名为 Editor 的目录 → Assembly-CSharp-Editor，其余 → Assembly-CSharp。
# 覆盖 Assets 下**每一条路径**，不依赖硬编码目录清单：asmdef 之外任何一层深处的残留都会被数出来。
$leftoverAll = @(Get-ChildItem (Join-Path $root 'Assets') -Recurse -File -Filter *.cs |
        Where-Object { -not $owner.ContainsKey($_.FullName) } |
        ForEach-Object { $_.FullName } | Sort-Object)

$gatePath       = Join-Path $outDir 'Gate.csproj'
$gateEditorPath = Join-Path $outDir 'GateEditor.csproj'

$leftoverRuntime = New-LeftoverProject -OutPath $gatePath -AssemblyName 'CompileGate' `
    -Files @($leftoverAll | Where-Object { $_ -notmatch '\\Editor\\' }) `
    -ExtraProjectRefs @()

$editorExtraRefs = @()
if (Test-Path $gatePath) { $editorExtraRefs += $gatePath }

$leftoverEditor = New-LeftoverProject -OutPath $gateEditorPath -AssemblyName 'CompileGateEditor' `
    -Files @($leftoverAll | Where-Object { $_ -match '\\Editor\\' }) `
    -ExtraProjectRefs $editorExtraRefs

if ($leftoverRuntime -gt 0) { $generated += $gatePath;       Write-Host ("  {0,-32} {1,4} 个 .cs（预定义 Assembly-CSharp 残留）" -f 'Assembly-CSharp', $leftoverRuntime) }
if ($leftoverEditor  -gt 0) { $generated += $gateEditorPath; Write-Host ("  {0,-32} {1,4} 个 .cs（预定义 Assembly-CSharp-Editor 残留）" -f 'Assembly-CSharp-Editor', $leftoverEditor) }

# ======================================================================
# 6 · 拓扑守卫 + 报告
# ======================================================================

$guardFailed = $false

if (-not $NoTopologyGuard) {
    foreach ($rel in $expectedAsmdefs.Keys) {
        $dir = Join-Path $root $rel
        if (-not (Test-Path $dir)) { continue }

        $want = $expectedAsmdefs[$rel]
        $mine = @($defs | Where-Object { $_.Dir -eq (Resolve-Path $dir).Path })

        if ($mine.Count -eq 0) {
            Write-Host "守卫：$rel 下没有 asmdef（应为 $want）—— 源码会静默退回 Assembly-CSharp" -ForegroundColor Red
            $guardFailed = $true
        }
        elseif ($mine[0].Name -ne $want) {
            Write-Host "守卫：$rel 的 asmdef 是 $($mine[0].Name)，应为 $want" -ForegroundColor Red
            $guardFailed = $true
        }
    }

    if ($violations.Count -gt 0) {
        Write-Host '守卫：发现反向依赖（层级只能单向：cfg ← Data ← Logic ← Presentation）：' -ForegroundColor Red
        foreach ($v in $violations) { Write-Host "  $v" -ForegroundColor Red }
        $guardFailed = $true
    }

    if ($missing.Count -gt 0) {
        Write-Host '守卫：这些引用解析不到（会变成 CS0246）：' -ForegroundColor Red
        foreach ($v in $missing) { Write-Host "  $v" -ForegroundColor Red }
        $guardFailed = $true
    }

    # 导表镜像覆盖区：LubanImport.MirrorCopy 会删掉暂存区里没有的**一切非 .meta 文件**。
    # asmdef 放进去 = 下次导表静默消失，该目录的源码退回 Assembly-CSharp，边界无声塌掉。
    # 这条是硬的实测结论，别把 cfg.asmdef 挪回 Generated/Config/。
    foreach ($rel in @('Assets\Scripts\Generated\Config', 'Assets\StreamingAssets\Luban')) {
        $dir = Join-Path $root $rel
        if (-not (Test-Path $dir)) { continue }

        foreach ($bad in (Get-ChildItem $dir -Recurse -File -Filter *.asmdef)) {
            Write-Host "守卫：$rel 是导表镜像覆盖区，asmdef 下次导表必被删除 —— 应放在 Generated/：$($bad.FullName)" -ForegroundColor Red
            $guardFailed = $true
        }
    }
}

if ($Topology) {
    Write-Host ''
    Write-Host '程序集拓扑（asmdef 声明的引用）：' -ForegroundColor Cyan
    foreach ($d in $defs) {
        Write-Host ("  {0,-30} ← {1}" -f $d.Name, (($d.Refs | Sort-Object) -join ', '))
    }
}

if ($guardFailed) { Write-Host ''; Write-Host '拓扑守卫未通过。' -ForegroundColor Red; exit 1 }

if ($NoBuild) { Write-Host '（-NoBuild：跳过编译）'; exit 0 }

# ======================================================================
# 7 · 编译
# ======================================================================

Write-Host ''
Write-Host '编译：' -ForegroundColor Cyan

$log = @()

foreach ($p in $generated) {
    $log += & dotnet build $p -v minimal -nologo 2>&1
}

$log | ForEach-Object { Write-Host $_ }

# 计数口径：**去重后的逐条诊断**。
# 两个坑各踩过一次：
#   ① 汇总行 `    1 个警告` 的缩进里含 `: `，被旧的 `: warning [A-Z]+\d+` 数成了诊断；
#   ② 一个工程被别的工程 ProjectReference 时会**重复编译**，同一条诊断打印多遍，
#      所以"匹配到几行"不等于"有几条警告"。
# 于是这里先按「路径(行,列) ＋ 级别 ＋ 编号」去重，再数。
function Get-DiagnosticCount {
    param(
        [object[]]$Log,
        [string]$Level
    )

    $seen = @{}

    foreach ($line in $Log) {
        $m = [regex]::Match([string]$line, '^(.*?\(\d+,\d+\)): ' + $Level + ' ([A-Z]+\d+):')

        if ($m.Success) { $seen[$m.Groups[1].Value + '|' + $m.Groups[2].Value] = $true }
    }

    return $seen.Count
}

$errors   = Get-DiagnosticCount -Log $log -Level 'error'
$warnings = Get-DiagnosticCount -Log $log -Level 'warning'

Write-Host ''
Write-Host ("错误 {0} 条 / 警告 {1} 条" -f $errors, $warnings) -ForegroundColor $(if ($errors -eq 0) { 'Green' } else { 'Red' })

if ($errors -gt 0) { exit 1 }
exit 0
