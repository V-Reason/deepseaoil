<#
Luban 生成物镜像拷贝（与 Assets/Editor/LubanImport.cs 的 MirrorCopy 同语义）。

为什么需要它：
  导表链路是「先写 ConfigWorkspace/output 暂存区 → 校验通过 → 镜像进工程」。
  在 Unity 里这一步由菜单触发；没有 Unity（CI / Agent）时缺了它就只能手拷，
  而手拷一定会漏掉「反向删除」——删掉的表会在 StreamingAssets 留下孤儿 JSON。

镜像规则（逐条对应 LubanImport.MirrorCopy）：
  ① dst 里 src 没有的**非 .meta** 文件 → 删除（反向删除，防孤儿）
  ② 内容不同的文件 → 覆盖；内容相同的 → 不动（保住生成的 .meta/GUID，git diff 干净）
  ③ 空目录回收
  ④ 额外：清理「对应文件已不存在」的孤儿 .meta
     （MirrorCopy 刻意不删 .meta；在 Unity 里脚本重编译才会回收，脱离 Unity 就成了垃圾）

用法：
  pwsh Tools/luban-mirror.ps1            # 预演（默认，不写任何文件）
  pwsh Tools/luban-mirror.ps1 -Apply     # 真正镜像

退出码：0 = 成功（含预演）；1 = 前置检查失败或拷贝出错。
#>
[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'ConfigWorkspace/output'
$pairs = @(
    @{ Src = Join-Path $stage 'code'; Dst = Join-Path $root 'Assets/Scripts/Generated/Config' },
    @{ Src = Join-Path $stage 'data'; Dst = Join-Path $root 'Assets/StreamingAssets/Luban' }
)

# 生成物目录里原本就有的手写件（不得被反向删除误伤）：目前为空。
# 注意 Assets/Scripts/Generated/cfg.asmdef 刻意放在上一级，不在镜像区里。
$keep = @()

foreach ($p in $pairs) {
    if (-not (Test-Path $p.Src)) {
        throw "暂存区不存在：$($p.Src)（先跑 Luban 导出，且不要带 -f / outputSaver=null）"
    }
}

$deletes = @()
$copies = @()
$metaDeletes = @()

function Get-Rel([string]$base, [string]$full) {
    $b = $base.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $full.Substring($b.Length)
}

function Get-Sha([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
}

foreach ($p in $pairs) {
    $srcFiles = @{}
    foreach ($f in Get-ChildItem -LiteralPath $p.Src -Recurse -File) {
        $srcFiles[(Get-Rel $p.Src $f.FullName)] = $f.FullName
    }

    if (Test-Path $p.Dst) {
        foreach ($f in Get-ChildItem -LiteralPath $p.Dst -Recurse -File) {
            $rel = Get-Rel $p.Dst $f.FullName
            if ($srcFiles.ContainsKey($rel)) { continue }
            if ($rel.EndsWith('.meta', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($keep -contains $rel) { continue }
            $deletes += $f.FullName
        }
    }

    foreach ($rel in $srcFiles.Keys) {
        $dstPath = Join-Path $p.Dst $rel
        if (Test-Path -LiteralPath $dstPath) {
            if ((Get-Sha $srcFiles[$rel]) -eq (Get-Sha $dstPath)) { continue }
        }
        $copies += [pscustomobject]@{ Src = $srcFiles[$rel]; Dst = $dstPath }
    }

    # 孤儿 .meta：镜像区里有 .meta，但同名文件在**目标区**已不存在
    # （暂存区里也不会有——它对应的是被删掉的表）。判据只看目标区，才抓得到
    # 「反向删除留下的 .meta」这一类。
    if (Test-Path $p.Dst) {
        foreach ($f in Get-ChildItem -LiteralPath $p.Dst -Recurse -File -Filter *.meta) {
            $rel = Get-Rel $p.Dst $f.FullName
            $stem = $rel.Substring(0, $rel.Length - 5)
            if (Test-Path -LiteralPath (Join-Path $p.Dst $stem)) { continue }
            $metaDeletes += $f.FullName
        }
    }
}

Write-Host ''
Write-Host '镜像计划：' -ForegroundColor Cyan
Write-Host ("  待删除（生成物区里多出来的非 .meta 文件）：{0}" -f $deletes.Count)
$deletes | Select-Object -First 20 | ForEach-Object { Write-Host "    - $_" }
Write-Host ("  待覆盖/新增：{0}" -f $copies.Count)
$copies | Select-Object -First 20 | ForEach-Object { Write-Host "    + $($_.Dst)" }
Write-Host ("  孤儿 .meta 待清：{0}" -f $metaDeletes.Count)
$metaDeletes | Select-Object -First 20 | ForEach-Object { Write-Host "    x $_" }

if (-not $Apply) {
    Write-Host ''
    Write-Host '预演结束（未写任何文件）。确认无误后加 -Apply。' -ForegroundColor Yellow
    exit 0
}

foreach ($path in $deletes) {
    Remove-Item -LiteralPath $path -Force
}

foreach ($c in $copies) {
    $dir = Split-Path -Parent $c.Dst
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item -LiteralPath $c.Src -Destination $c.Dst -Force
}

foreach ($path in $metaDeletes) {
    Remove-Item -LiteralPath $path -Force
}

# 空目录回收（与 MirrorCopy 一致）
foreach ($p in $pairs) {
    if (-not (Test-Path $p.Dst)) { continue }
    Get-ChildItem -LiteralPath $p.Dst -Recurse -Directory |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            if (Test-Path $_.FullName) {
                if (-not (Get-ChildItem -LiteralPath $_.FullName -Force)) {
                    Remove-Item -LiteralPath $_.FullName -Force
                }
            }
        }
}

Write-Host ''
Write-Host ("镜像完成：删除 {0}，拷贝 {1}，清孤儿 .meta {2}" -f $deletes.Count, $copies.Count, $metaDeletes.Count) -ForegroundColor Green
exit 0
