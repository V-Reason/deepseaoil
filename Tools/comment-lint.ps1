<#
注释守卫（Comment Lint）—— 本仓注释规范的唯一机器判据。

它解决什么问题：
  注释不像代码，编译器和测试都不会因为它变多而报错。上一轮把全仓注释压下去之后，
  后续开发又把旧风格的啰嗦注释带了回来（注释字符 53,893 → 69,704，+29%），
  而且没有任何机器判据能拦住它。本脚本就是那条判据。

三条判据：
  ① 预算：每个文件的「注释有效字符数」不得超过 Tools/comment-budget.json 里登记的上限。
          预算只降不升；确需上调必须在 PR 里显式说明并重跑 -Mode seal。
  ② 标签：注释里出现 <summary> / <remarks> 以外的 XML 标签即为违规
          （<see cref> 必须降级成纯文本类型名）；<summary>/<remarks> 必须在本行内开闭。
  ③ 代码零改动：-Mode verify -Base <rev> 逐行比对非注释代码与某个 git 版本是否一致。

覆盖面：Assets/Scripts/** 与 Assets/Tests/**（测试也是手写代码，同样会单调膨胀）。

口径（与预算表同源，别另立一套）：
  逐行取注释正文，剔除 `//`、`///`、`/* */` 标记；行首行尾空白不计、换行符不计；
  其余（文字/标点/空格/XML 标签）全部计入。// 与 /* */ 均按 C# 词法处理，
  字符串字面量与字符字面量里的 `//` 不会被误判成注释。

用法：
  pwsh Tools/comment-lint.ps1                    # = -Mode check，跑全部判据，违规 exit 1
  pwsh Tools/comment-lint.ps1 -Mode report       # 打印逐文件预算/现值/余量表（按余量升序）
  pwsh Tools/comment-lint.ps1 -Mode report -Top 30
  pwsh Tools/comment-lint.ps1 -Mode tags         # 只查 XML 标签白名单
  pwsh Tools/comment-lint.ps1 -Mode verify -Base e2f08be   # 证明「只改了注释」
  pwsh Tools/comment-lint.ps1 -Mode seal         # 清理验收后，用当前工作区重新封印预算
  pwsh Tools/comment-lint.ps1 -Mode check -StagedOnly      # 只看 git 已暂存/已改动的 .cs

退出码：0 = 通过；1 = 有违规或前置检查失败。
基线（2026-10，Round-2 验收后）：172 个手写文件，注释 57,233 字符 / 代码 10,868 行 = 密度 5.27。
#>
[CmdletBinding()]
param(
    [ValidateSet('check', 'report', 'tags', 'verify', 'seal')]
    [string]$Mode = 'check',
    [string]$Root = '',
    [string]$Base = '',
    [int]$Top = 20,
    [switch]$StagedOnly
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $Root) { $Root = Split-Path -Parent $scriptDir }
$budgetPath = Join-Path $scriptDir 'comment-budget.json'
$excludeRe = '\\Generated\\'

# ======================================================================
# 0 · 新文件密度上限（未登记进预算表的文件按此判定）
# ======================================================================

$densityCap = 5.37

# ======================================================================
# 1 · C# 词法扫描：把源码切成「代码行」与「注释正文」
# ======================================================================

function Scan-Cs {
    param([string]$Text)

    $Text = $Text.Replace([string][char]0xFEFF, '')
    $len = $Text.Length
    $i = 0
    $line = 0
    $codeCur = New-Object System.Text.StringBuilder
    $comCur = New-Object System.Text.StringBuilder
    $codeLines = New-Object System.Collections.ArrayList
    $comLines = New-Object System.Collections.ArrayList
    $comChars = 0

    while ($i -lt $len) {
        $c = $Text[$i]

        if ($c -eq "`n") {
            [void]$codeLines.Add($codeCur.ToString()); [void]$comLines.Add($comCur.ToString().Trim())
            $codeCur.Clear() | Out-Null; $comCur.Clear() | Out-Null
            $line++; $i++; continue
        }
        if ($c -eq "`r") { $i++; continue }

        if ($c -eq '/' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '/') {
            $i += 2
            while ($i -lt $len -and $Text[$i] -ne "`n") {
                if ($Text[$i] -ne "`r") { [void]$comCur.Append($Text[$i]); $comChars++ }
                $i++
            }
            continue
        }
        if ($c -eq '/' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '*') {
            $i += 2
            while ($i -lt $len) {
                if ($Text[$i] -eq '*' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '/') { $i += 2; break }
                if ($Text[$i] -eq "`n") {
                    [void]$codeLines.Add($codeCur.ToString()); [void]$comLines.Add($comCur.ToString().Trim())
                    $codeCur.Clear() | Out-Null; $comCur.Clear() | Out-Null
                    $line++; $i++; continue
                }
                if ($Text[$i] -ne "`r") { [void]$comCur.Append($Text[$i]); $comChars++ }
                $i++
            }
            continue
        }
        if ($c -eq '@' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '"') {
            [void]$codeCur.Append('@"'); $i += 2
            while ($i -lt $len) {
                $d = $Text[$i]
                if ($d -eq "`n") {
                    [void]$codeLines.Add($codeCur.ToString()); [void]$comLines.Add('')
                    $codeCur.Clear() | Out-Null; $line++; $i++; continue
                }
                if ($d -eq '"' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '"') { [void]$codeCur.Append('""'); $i += 2; continue }
                if ($d -eq '"') { [void]$codeCur.Append('"'); $i++; break }
                [void]$codeCur.Append($d); $i++
            }
            continue
        }
        if ($c -eq '"') {
            [void]$codeCur.Append('"'); $i++
            while ($i -lt $len) {
                $d = $Text[$i]
                if ($d -eq "`n") {
                    [void]$codeLines.Add($codeCur.ToString()); [void]$comLines.Add('')
                    $codeCur.Clear() | Out-Null; $line++; $i++; continue
                }
                if ($d -eq '\') { if ($i + 1 -lt $len) { [void]$codeCur.Append($d).Append($Text[$i + 1]); $i += 2; continue } }
                if ($d -eq '"') { [void]$codeCur.Append('"'); $i++; break }
                [void]$codeCur.Append($d); $i++
            }
            continue
        }
        if ($c -eq "'") {
            [void]$codeCur.Append("'"); $i++
            while ($i -lt $len) {
                $d = $Text[$i]
                if ($d -eq '\') { if ($i + 1 -lt $len) { [void]$codeCur.Append($d).Append($Text[$i + 1]); $i += 2; continue } }
                if ($d -eq "'") { [void]$codeCur.Append("'"); $i++; break }
                [void]$codeCur.Append($d); $i++
            }
            continue
        }
        if ($c -ne ' ' -and $c -ne "`t") { [void]$codeCur.Append($c) }
        $i++
    }
    [void]$codeLines.Add($codeCur.ToString()); [void]$comLines.Add($comCur.ToString().Trim())

    [pscustomobject]@{
        Lines    = $line + 1
        ComChars = $comChars
        Code     = $codeLines
        Com      = $comLines
    }
}

# 注释行里出现的 XML 标签名。
# 两个刻意放宽的地方，防误报：
#   · 单字母大写名（Clear<T>()、Span<T> 这类泛型书写）不算标签；
#   · 必须像标签（有闭合 `</x>` / 自闭合 `<x/>` / 带属性 `<x ...>` / 成对出现）才判。
function Get-BadTags {
    param([string[]]$ComLines)

    $ok = @('summary', 'remarks')
    $bad = New-Object System.Collections.ArrayList
    foreach ($cl in $ComLines) {
        if ([string]::IsNullOrEmpty($cl)) { continue }
        foreach ($m in [regex]::Matches($cl, '</?([A-Za-z][A-Za-z0-9_.]*)(\s[^<>]*)?/?>')) {
            $name = $m.Groups[1].Value
            if ($name -in $ok) { continue }
            # 泛型占位：单个大写字母，如 <T> / <TKey>
            if ($name -cmatch '^[A-Z][A-Za-z0-9]?$' -and $name.Length -le 2) { continue }
            [void]$bad.Add($name)
        }
    }
    return @($bad | Sort-Object -Unique)
}

# 「注释一律单行」的可判定形态：**任何一条注释行内部，`<summary>` / `<remarks>` 的开闭必须配平**。
# 拆行写（`/// <remarks>` 换行接正文再接 `/// </remarks>`）就是这里要拦的形态；
# 同一行里 `<summary>…</summary>` 与另一行 `<remarks>…</remarks>` 是两条独立注释，合法。
function Get-UnbalancedLines {
    param([string[]]$ComLines)

    $bad = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $ComLines.Count; $i++) {
        $cl = $ComLines[$i]
        if ([string]::IsNullOrEmpty($cl)) { continue }
        foreach ($tag in @('summary', 'remarks')) {
            $open = ([regex]::Matches($cl, "<$tag(\s[^<>]*)?>")).Count
            $close = ([regex]::Matches($cl, "</$tag>")).Count
            if ($open -ne $close) {
                # Scan-Cs 的 Com 数组是逐物理行产出的，下标 = 行号 - 1
                [void]$bad.Add([pscustomobject]@{ Line = ($i + 1); Tag = $tag; Text = $cl })
                break
            }
        }
    }
    return $bad
}

# ======================================================================
# 2 · 文件清单
# ======================================================================

function Get-TargetFiles {
    if ($StagedOnly) {
        $rel = & git -C $Root diff --name-only --cached --diff-filter=ACMR 2>$null
        $rel += & git -C $Root diff --name-only --diff-filter=ACMR 2>$null
        $out = New-Object System.Collections.ArrayList
        foreach ($r in $rel) {
            if ($r -notmatch '\.cs$') { continue }
            if ($r -notmatch '^Assets/(Scripts|Tests)/') { continue }
            if ($r -match 'Generated/') { continue }
            $p = Join-Path $Root ($r -replace '/', '\')
            if (Test-Path -LiteralPath $p) { [void]$out.Add((Get-Item -LiteralPath $p)) }
        }
        return @($out | Sort-Object FullName -Unique)
    }

    return @(Get-ChildItem -Path (Join-Path $Root 'Assets\Scripts'), (Join-Path $Root 'Assets\Tests') `
                -Recurse -Filter *.cs -File |
            Where-Object { $_.FullName -notmatch $excludeRe } | Sort-Object FullName)
}

function Get-RelPath {
    param([string]$FullName)
    return ($FullName.Replace("$Root\", '') -replace '\\', '/')
}

# ======================================================================
# 3 · 读预算表
# ======================================================================

function Read-Budget {
    if (-not (Test-Path -LiteralPath $budgetPath)) { return $null }
    $j = Get-Content -Raw -LiteralPath $budgetPath | ConvertFrom-Json
    $map = @{}
    foreach ($p in $j.files.PSObject.Properties) { $map[$p.Name] = [int]$p.Value }
    if ($j.densityCap) { $script:densityCap = [double]$j.densityCap }
    return [pscustomobject]@{ Map = $map; Raw = $j }
}

# ======================================================================
# 4 · -Mode seal / report / check / tags / verify
# ======================================================================

$files = Get-TargetFiles

# ---------- seal ----------
if ($Mode -eq 'seal') {
    $old = Read-Budget
    $rows = [ordered]@{}
    foreach ($f in $files) {
        $s = Scan-Cs -Text ([System.IO.File]::ReadAllText($f.FullName))
        $rows[(Get-RelPath $f.FullName)] = $s.ComChars
    }

    $head = & git -C $Root rev-parse --short HEAD 2>$null
    $obj = [ordered]@{
        note      = '注释预算表：每个文件的注释有效字符上限。只降不升；上调必须走 PR 说明 + -Mode seal。口径见 Docs/注释规范.md。覆盖面：Assets/Scripts/** 与 Assets/Tests/**；测试文件按文件单独登记（测试的注释/代码比天然高于实现文件，不吃 densityCap）。'
        sealedAt  = [string]$head
        sealedOn  = (Get-Date -Format 'yyyy-MM-dd')
        densityCap = $densityCap
        files     = $rows
    }
    $obj | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $budgetPath -Encoding utf8

    $sum = ($rows.Values | Measure-Object -Sum).Sum
    Write-Host ("封印完成：{0} 个文件，注释合计 {1} 字符  (sealedAt={2})" -f $rows.Count, $sum, $head) -ForegroundColor Green
    if ($old) {
        $up = @(); $down = @()
        foreach ($k in $rows.Keys) {
            if (-not $old.Map.ContainsKey($k)) { $up += "+$k=$($rows[$k])（新登记）"; continue }
            if ($rows[$k] -gt $old.Map[$k]) { $up += "+${k}: $($old.Map[$k]) → $($rows[$k])" }
            elseif ($rows[$k] -lt $old.Map[$k]) { $down += "-${k}: $($old.Map[$k]) → $($rows[$k])" }
        }
        foreach ($k in $old.Map.Keys) { if (-not $rows.Contains($k)) { $down += "-$k（文件已消失）" } }
        if ($up.Count) { Write-Host "`n预算上调（必须在 PR 里解释）：" -ForegroundColor Yellow; $up | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow } }
        if ($down.Count) { Write-Host "`n预算下调：$($down.Count) 个文件"; $down | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" } }
    }
    exit 0
}

# ---------- 逐文件度量 ----------
$rows = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $rel = Get-RelPath $f.FullName
    $s = Scan-Cs -Text ([System.IO.File]::ReadAllText($f.FullName))
    [void]$rows.Add([pscustomobject]@{
            File    = $rel
            C       = $s.ComChars
            CodeLn  = @($s.Code | Where-Object { $_ -ne '' }).Count
            Density = 0.0
            Budget  = 0
            Over    = 0
            BadTags = @()
        })
}
foreach ($r in $rows) {
    if ($r.CodeLn -gt 0) { $r.Density = [math]::Round($r.C / $r.CodeLn, 2) }
}

# ---------- tags ----------
if ($Mode -eq 'tags') {
    $hit = 0
    foreach ($f in $files) {
        $s = Scan-Cs -Text ([System.IO.File]::ReadAllText($f.FullName))
        $bad = Get-BadTags -ComLines $s.Com
        $unbal = Get-UnbalancedLines -ComLines $s.Com
        if ($bad.Count -eq 0 -and $unbal.Count -eq 0) { continue }
        $hit++
        $rel = Get-RelPath $f.FullName
        if ($bad.Count -gt 0) { Write-Host ("{0}`n    违规标签: {1}" -f $rel, ($bad -join ', ')) -ForegroundColor Red }
        foreach ($u in ($unbal | Select-Object -First 5)) {
            Write-Host ("{0}:{1}`n    跨行注释: <{2}> 未在本行闭合" -f $rel, $u.Line, $u.Tag) -ForegroundColor Red
        }
    }
    if ($hit -eq 0) {
        Write-Host ("TAGS OK：{0} 个文件，注释里只出现 <summary>/<remarks>，且标签均在单行内开闭" -f $files.Count) -ForegroundColor Green
        exit 0
    }
    Write-Host ("`n共 {0} 个文件有注释形态违规。" -f $hit) -ForegroundColor Red
    exit 1
}

# ---------- verify ----------
if ($Mode -eq 'verify') {
    if (-not $Base) { throw '-Mode verify 需要 -Base <git rev>（例：-Base e2f08be）' }

    $problems = New-Object System.Collections.ArrayList
    $checked = 0; $newFiles = 0
    foreach ($f in $files) {
        $rel = Get-RelPath $f.FullName
        $baseText = & git -C $Root show "${Base}:$rel" 2>$null
        if ($LASTEXITCODE -ne 0) { $newFiles++; continue }
        $checked++
        $o = Scan-Cs -Text ($baseText -join "`n")
        $n = Scan-Cs -Text ([System.IO.File]::ReadAllText($f.FullName))

        $oc = @($o.Code | Where-Object { $_ -ne '' } | ForEach-Object { $_ -replace '\s', '' })
        $nc = @($n.Code | Where-Object { $_ -ne '' } | ForEach-Object { $_ -replace '\s', '' })

        if ($oc.Count -ne $nc.Count) {
            [void]$problems.Add("$rel : 代码行数 $($oc.Count) → $($nc.Count)")
            continue
        }
        for ($k = 0; $k -lt $oc.Count; $k++) {
            if ($oc[$k] -ne $nc[$k]) {
                [void]$problems.Add("$rel : 代码第 $($k + 1) 行不同  '$($oc[$k])'  →  '$($nc[$k])'")
                break
            }
        }
    }
    if ($problems.Count -gt 0) {
        Write-Host ("代码本体被改动（{0} 处）：" -f $problems.Count) -ForegroundColor Red
        $problems | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host ("VERIFY PASS：{0} 个文件的非注释代码与 {1} 逐行一致（{2} 个文件在 {1} 里不存在，跳过）" -f $checked, $Base, $newFiles) -ForegroundColor Green
    exit 0
}

# ---------- 预算判定 ----------
$budget = Read-Budget
if (-not $budget) {
    if ($Mode -eq 'report') { $budget = [pscustomobject]@{ Map = @{}; Raw = $null } }
    else { throw "找不到预算表：$budgetPath（先跑 pwsh Tools/comment-lint.ps1 -Mode seal）" }
}

$violations = New-Object System.Collections.ArrayList
foreach ($f in $files) {
    $rel = Get-RelPath $f.FullName
    $s = Scan-Cs -Text ([System.IO.File]::ReadAllText($f.FullName))
    $row = $rows | Where-Object { $_.File -eq $rel }
    $bad = Get-BadTags -ComLines $s.Com
    $unbal = Get-UnbalancedLines -ComLines $s.Com
    $row.BadTags = $bad

    if ($budget.Map.ContainsKey($rel)) { $row.Budget = $budget.Map[$rel] }
    else { $row.Budget = [math]::Floor($densityCap * $row.CodeLn) }

    $row.Over = $row.C - $row.Budget
    if ($row.Over -gt 0) {
        # 注意：-f 的逗号参数表在**方法实参**里会被当成参数分隔符，必须再套一层括号。
        [void]$violations.Add(("[预算] {0}`n        注释 {1} 字符 > 上限 {2}（超 {3}）" -f $rel, $row.C, $row.Budget, $row.Over))
    }
    if ($bad.Count -gt 0) {
        [void]$violations.Add(("[标签] {0}`n        违规标签: {1}" -f $rel, ($bad -join ', ')))
    }
    if ($unbal.Count -gt 0) {
        $first = $unbal[0]
        [void]$violations.Add(("[跨行] {0}:{1}`n        <{2}> 未在本行闭合 —— 注释必须单行" -f $rel, $first.Line, $first.Tag))
    }
}

# 预算表里的孤儿条目（文件被删/改名）
$orphans = @($budget.Map.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Root ($_ -replace '/', '\'))) })

if ($Mode -eq 'report') {
    $rows | Sort-Object Over -Descending | Select-Object -First $Top File, C, Budget, Over, Density, CodeLn |
        Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    $totC = ($rows | Measure-Object C -Sum).Sum
    $totL = ($rows | Measure-Object CodeLn -Sum).Sum
    $totB = ($rows | Measure-Object Budget -Sum).Sum
    Write-Host ("合计：注释 {0} 字符 / 代码 {1} 行 = 密度 {2}（新文件上限 {3}）" -f `
            $totC, $totL, [math]::Round($totC / $totL, 3), $densityCap)
    Write-Host ("预算合计 {0}；超上限文件 {1} 个" -f $totB, @($rows | Where-Object { $_.Over -gt 0 }).Count)
    if ($orphans.Count) { Write-Host ("预算表孤儿条目 {0} 个（文件已不存在，建议重跑 -Mode seal）" -f $orphans.Count) -ForegroundColor Yellow }
    exit 0
}

# ---------- check ----------
$totC = ($rows | Measure-Object C -Sum).Sum
$totL = ($rows | Measure-Object CodeLn -Sum).Sum
Write-Host ("注释 {0} 字符 / 代码 {1} 行 = 密度 {2}；文件 {3} 个；超上限 {4} 个" -f `
        $totC, $totL, [math]::Round($totC / $totL, 3), $rows.Count, @($rows | Where-Object { $_.Over -gt 0 }).Count)

if ($orphans.Count) {
    Write-Host ("`n预算表孤儿条目 {0} 个（文件已不存在）：" -f $orphans.Count) -ForegroundColor Yellow
    $orphans | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    Write-Host '  这不是失败，但预算表该重跑 -Mode seal 了。' -ForegroundColor Yellow
}

if ($violations.Count -gt 0) {
    Write-Host ("`n违规 {0} 条：" -f $violations.Count) -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "`n规范见 Docs/工程/注释规范.md" -ForegroundColor Red
    exit 1
}

Write-Host 'LINT OK：全部文件在预算内，且注释里没有白名单之外的 XML 标签。' -ForegroundColor Green
exit 0
