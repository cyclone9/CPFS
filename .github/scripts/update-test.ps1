# Run by .github/workflows/ci.yml on the Windows runner, from the repository root (PowerShell 7).
# Usage: pwsh .github/scripts/update-test.ps1
#   Builds two test-hook builds of CPFS (-p:CpfsUpdateTest=true): A with the repository's version and B as 99.0.0.
#   Folders stand in for the GitHub release (latest.json, Cpfs-<V>.exe and its .sha256 written by release-check.sh).
#   A updates itself from each folder while it runs, headless: the good case must swap, start B, and leave no
#   leftovers once A has exited; every bad case must leave A's file exactly as it was. Exit 0 when every check passed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Get-Location).Path
$temp = Join-Path $env:RUNNER_TEMP 'cpfs-update-test'
$bash = Join-Path $env:ProgramFiles 'Git\bin\bash.exe'
$rows = [System.Collections.Generic.List[string]]::new()
$failed = 0

function Check([bool]$pass, [string]$name, [string]$detail = '') {
  $script:rows.Add(($(if ($pass) { 'PASS  ' } else { 'FAIL  ' }) + $name + $(if ($detail) { "  ($detail)" } else { '' })))
  if (-not $pass) { $script:failed++ }
}

function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Build([string]$name, [string[]]$extra) {
  $src = Join-Path $temp "src-$name"
  # .git comes along, so the build stamps the same "+<commit>" as the normal one.
  & robocopy $root $src /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
  if ($LASTEXITCODE -ge 8) { throw "robocopy for $name failed with $LASTEXITCODE" }
  $global:LASTEXITCODE = 0
  & dotnet build (Join-Path $src 'src\Cpfs\Cpfs.csproj') -c Release -p:CpfsUpdateTest=true @extra | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "build $name failed" }
  return (Join-Path $src 'src\Cpfs\bin\Release\net48\Cpfs.exe')
}

New-Item -ItemType Directory -Force -Path $temp | Out-Null
$exeA = Build 'a' @()
$exeB = Build 'b' @('-p:VersionPrefix=99.0.0', '-p:VersionSuffix=')
$versionA = ((Get-Item -LiteralPath $exeA).VersionInfo.ProductVersion -split '\+')[0]
$versionB = ((Get-Item -LiteralPath $exeB).VersionInfo.ProductVersion -split '\+')[0]
$hashA = Hash $exeA
$hashB = Hash $exeB
Write-Host "A $versionA $hashA"
Write-Host "B $versionB $hashB"
Check ($versionB -eq '99.0.0') 'B is version 99.0.0' $versionB

# The release guard's detector, proven the other way round: a hooks build must report itself as one.
$selftestOut = Join-Path $temp 'selftest-a.txt'
$p = Start-Process -FilePath $exeA -ArgumentList '--selftest' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $selftestOut
$hooksLine = @(Get-Content -LiteralPath $selftestOut | Where-Object { $_ -like 'Update test hooks:*' }) -join ' | '
Check ($hooksLine -eq 'Update test hooks: present') 'the hooks build self-test reports the hooks as present' "$hooksLine, exit $($p.ExitCode)"

$template = (Get-Content -LiteralPath (Join-Path $root 'tests\Cpfs.Tests\Fixtures\update\latest-0.8.0.json') -Raw).Replace('{REPO}', 'cyclone9/CPFS')

# A release folder: the exe as Cpfs-<V>.exe, its .sha256 from the release's own command, and latest.json.
function New-Release([string]$name, [string]$version, [string]$exe, [scriptblock]$change = $null) {
  $dir = Join-Path $temp "fx\$name"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $asset = "Cpfs-$version.exe"
  Copy-Item -LiteralPath $exe -Destination (Join-Path $dir $asset) -Force
  # release-check.sh refuses a suffixed version, as the release does; such a tag is refused before any download.
  if ($version -notmatch '-') {
    & $bash .github/scripts/release-check.sh checksum $version $dir.Replace('\', '/') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "checksum for $name failed" }
  }
  $json = $template | ConvertFrom-Json -AsHashtable
  $json.tag_name = "v$version"
  $json.html_url = "https://github.com/cyclone9/CPFS/releases/tag/v$version"
  $json.assets[0].name = $asset
  $json.assets[0].size = (Get-Item -LiteralPath (Join-Path $dir $asset)).Length
  $json.assets[0].digest = 'sha256:' + (Hash (Join-Path $dir $asset))
  $json.assets[1].name = "$asset.sha256"
  if ($change) { & $change $json $dir }
  [System.IO.File]::WriteAllText((Join-Path $dir 'latest.json'), ($json | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
  return $dir
}

# Copies A into a fresh folder and lets it update itself from $release, with the given test switches.
function Invoke-Update([string]$case, [string]$release, [hashtable]$env = @{}, [scriptblock]$prepare = $null) {
  $dir = Join-Path $temp "run\$case"
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $exe = Join-Path $dir 'Cpfs.exe'
  Copy-Item -LiteralPath $exeA -Destination $exe -Force
  if ($prepare) { & $prepare $dir }
  $all = @{ CPFS_UPDATE_TEST_HEADLESS = '1' } + $env
  foreach ($k in $all.Keys) { Set-Item -Path "env:$k" -Value $all[$k] }
  $out = Join-Path $temp "out-$case.txt"
  try {
    $p = Start-Process -FilePath $exe -ArgumentList '--ci-update-test', $release -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out
  } finally {
    foreach ($k in $all.Keys) { Remove-Item -Path "env:$k" -ErrorAction SilentlyContinue }
  }
  $text = Get-Content -LiteralPath $out -Raw
  Write-Host "--- $case (exit $($p.ExitCode)) ---"
  Write-Host $text
  $outcome = ($text -split "`r?`n" | Where-Object { $_ -like 'OUTCOME: *' } | Select-Object -First 1)
  return [pscustomobject]@{ Dir = $dir; Exe = $exe; Exit = $p.ExitCode; Outcome = $(if ($outcome) { $outcome.Substring(9) } else { '(none)' }) }
}

function Files([string]$dir) { @(Get-ChildItem -LiteralPath $dir -Force | ForEach-Object Name | Sort-Object) -join ', ' }

# Nothing replaced: A's file unchanged and nothing else beside it.
function Unchanged($r, [string]$case, [string]$want) {
  Check ($r.Exit -eq 0 -and $r.Outcome -like $want) "$case`: outcome $want" "$($r.Outcome), exit $($r.Exit)"
  Check ((Hash $r.Exe) -eq $hashA) "$case`: the exe is still A"
  Check ((Files $r.Dir) -eq 'Cpfs.exe') "$case`: nothing else in the folder" (Files $r.Dir)
}

$good = New-Release 'good' '99.0.0' $exeB

# The good case: A replaces itself while running, B starts, says so, and removes A's old file after A has exited.
$r = Invoke-Update 'good' $good
Check ($r.Outcome -eq 'Restart:Started') 'good: swapped and the new version started' $r.Outcome
Check ((Hash $r.Exe) -eq $hashB) 'good: the exe is now B'
$old = "$($r.Exe).cpfs-old"
$deadline = (Get-Date).AddSeconds(45)
while ((Test-Path -LiteralPath $old) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
Check ((Files $r.Dir) -eq 'Cpfs.exe') 'good: the old exe and the staging file are gone once A has exited' (Files $r.Dir)
$p = Start-Process -FilePath $r.Exe -ArgumentList '--update-probe', '99.0.0' -Wait -PassThru
Check ($p.ExitCode -eq 67) 'good: B answers the probe from the final path' "exit $($p.ExitCode)"

$r = Invoke-Update 'wrong-hash' (New-Release 'wrong-hash' '99.0.0' $exeB { param($j, $d) [System.IO.File]::WriteAllText((Join-Path $d 'Cpfs-99.0.0.exe.sha256'), ('0' * 64) + "  Cpfs-99.0.0.exe`n") })
Unchanged $r 'wrong hash' 'Error:Damaged'

$r = Invoke-Update 'bad-sha-line' (New-Release 'bad-sha-line' '99.0.0' $exeB { param($j, $d) [System.IO.File]::WriteAllText((Join-Path $d 'Cpfs-99.0.0.exe.sha256'), ($j.assets[0].digest.Substring(7)) + " *Cpfs-99.0.0.exe`n") })
Unchanged $r 'malformed .sha256' 'Error:Damaged'

$r = Invoke-Update 'version-inside' (New-Release 'version-inside' '98.0.0' $exeB)
Unchanged $r 'exe version differs from the tag' 'Error:WrongVersion'

$r = Invoke-Update 'not-greater' (New-Release 'not-greater' '0.1.0' $exeB)
Unchanged $r 'tag not greater' 'NoOffer:*'

$r = Invoke-Update 'suffix' (New-Release 'suffix' '99.0.0-rc1' $exeB)
Unchanged $r 'tag with a suffix' 'NoOffer:TagNotPlain'

$r = Invoke-Update 'missing-file' (New-Release 'missing-file' '99.0.0' $exeB { param($j, $d) Remove-Item -LiteralPath (Join-Path $d 'Cpfs-99.0.0.exe.sha256') })
Unchanged $r 'missing .sha256 file' 'Error:Missing'

$r = Invoke-Update 'missing-asset' (New-Release 'missing-asset' '99.0.0' $exeB { param($j, $d) $j.assets = @($j.assets[0]) })
Unchanged $r 'missing asset in the release' 'NoOffer:ShaAssetMissing'

$r = Invoke-Update 'over-cap' (New-Release 'over-cap' '99.0.0' $exeB { param($j, $d) $j.assets[0].size = 70000000 })
Unchanged $r 'size over the cap' 'NoOffer:TooLarge'

$r = Invoke-Update 'digest' (New-Release 'digest' '99.0.0' $exeB { param($j, $d) $j.assets[0].digest = 'sha256:' + ('0' * 64) })
Unchanged $r 'API digest disagrees' 'Error:Damaged'

$r = Invoke-Update 'size' (New-Release 'size' '99.0.0' $exeB { param($j, $d) $j.assets[0].size = $j.assets[0].size + 1 })
Unchanged $r 'API size disagrees' 'Error:Damaged'

$r = Invoke-Update 'probe' $good @{ CPFS_UPDATE_TEST_FAIL_PROBE = '1' }
Unchanged $r 'failing probe' 'Error:ProbeFailed'

$r = Invoke-Update 'second-rename' $good @{ CPFS_UPDATE_TEST_FAIL_SECOND_RENAME = '1' }
Unchanged $r 'failing second rename (first undone)' 'Error:SwapFailed'

# The new file can't be read back after the swap (an antivirus lock): A goes back, B is kept aside as .cpfs-failed.
$r = Invoke-Update 'read-back' $good @{ CPFS_UPDATE_TEST_FAIL_READBACK = '1' }
Check ($r.Exit -eq 0 -and $r.Outcome -eq 'Error:SwapFailed') 'failing read-back: outcome Error:SwapFailed' "$($r.Outcome), exit $($r.Exit)"
Check ((Hash $r.Exe) -eq $hashA) 'failing read-back: the exe is A again'
$failedFile = "$($r.Exe).cpfs-failed"
Check ((Test-Path -LiteralPath $failedFile) -and (Hash $failedFile) -eq $hashB) 'failing read-back: B kept aside as .cpfs-failed'
Check ((Files $r.Dir) -eq 'Cpfs.exe, Cpfs.exe.cpfs-failed') 'failing read-back: no old or staging file' (Files $r.Dir)

# The folder denies this account "create files": the staging file is refused, the verified bytes go to Save as.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$r = Invoke-Update 'deny-write' $good @{} { param($d) & icacls $d /deny "*${sid}:(WD)" | Out-Host }
& icacls $r.Dir /remove:d "*$sid" | Out-Host
Unchanged $r 'folder with a deny-write entry' 'SaveAs'
$saved = Join-Path $env:RUNNER_TEMP 'cpfs-saved-Cpfs-99.0.0.exe'
Check ((Test-Path -LiteralPath $saved) -and (Hash $saved) -eq $hashB) 'deny-write: Save as wrote the verified file'

# B fails at start: A puts itself back and keeps B aside as .cpfs-failed.
$r = Invoke-Update 'start-fails' $good @{ CPFS_UPDATE_TEST_FAIL_START = '1' }
Check ($r.Outcome -eq 'Restart:RollBack') 'start fails: rolled back' $r.Outcome
Check ((Hash $r.Exe) -eq $hashA) 'start fails: the exe is A again'
$failedFile = "$($r.Exe).cpfs-failed"
Check ((Test-Path -LiteralPath $failedFile) -and (Hash $failedFile) -eq $hashB) 'start fails: B kept aside as .cpfs-failed'
Check ((Files $r.Dir) -eq 'Cpfs.exe, Cpfs.exe.cpfs-failed') 'start fails: no old or staging file' (Files $r.Dir)

$log = Join-Path $env:LOCALAPPDATA 'CPFS\cpfs-log.txt'
if (Test-Path -LiteralPath $log) { Write-Host '--- update lines in the log ---'; Select-String -LiteralPath $log -SimpleMatch '[update]' | ForEach-Object Line | Select-Object -Last 200 }

Write-Host ''
Write-Host 'Result:'
$rows | ForEach-Object { Write-Host $_ }
Write-Host $(if ($failed -eq 0) { "All $($rows.Count) checks passed" } else { "$failed of $($rows.Count) checks failed" })
exit $(if ($failed -eq 0) { 0 } else { 1 })
