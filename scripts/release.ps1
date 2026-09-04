# Release script: bump the csproj <Version>, test, commit, tag, push.
# The pushed v* tag triggers CI, which builds the Velopack installer + update
# packages and publishes them to a GitHub Release; installed apps then auto-update.
#
# Usage:
#   .\scripts\release.ps1                # patch bump  (1.2.3 -> 1.2.4)
#   .\scripts\release.ps1 minor         # minor bump  (1.2.3 -> 1.3.0)
#   .\scripts\release.ps1 major         # major bump  (1.2.3 -> 2.0.0)
#   .\scripts\release.ps1 1.4.0         # explicit version
#   .\scripts\release.ps1 -NoTest       # skip tests
#   .\scripts\release.ps1 -Watch        # follow the CI run after pushing
#
# Works in any repo whose app csproj contains a <Version> property (auto-detected).
param(
    [string]$Bump = "patch",
    [string]$Project = "",
    [switch]$NoTest,
    [switch]$Watch
)

$ErrorActionPreference = "Stop"
$repoRoot = git rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0) { throw "Not inside a git repository." }
Set-Location $repoRoot

# ---- locate the versioned csproj ----
if (-not $Project) {
    $candidates = Get-ChildItem -Recurse -Filter *.csproj |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Where-Object { (Get-Content $_.FullName -Raw) -match '<Version>' }
    if ($candidates.Count -eq 0) { throw "No csproj with a <Version> property found. Add one, e.g. <Version>0.1.0</Version>." }
    if ($candidates.Count -gt 1) { throw "Multiple csprojs carry <Version>: $($candidates.FullName -join ', '). Pass -Project." }
    $Project = $candidates[0].FullName
}
$raw = Get-Content $Project -Raw
if ($raw -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw "$Project has no <Version>x.y.z</Version> property." }
$cur = [version]("{0}.{1}.{2}" -f $Matches[1], $Matches[2], $Matches[3])

# ---- compute the new version ----
switch -Regex ($Bump) {
    '^patch$' { $new = [version]::new($cur.Major, $cur.Minor, $cur.Build + 1) }
    '^minor$' { $new = [version]::new($cur.Major, $cur.Minor + 1, 0) }
    '^major$' { $new = [version]::new($cur.Major + 1, 0, 0) }
    '^\d+\.\d+\.\d+$' { $new = [version]$Bump }
    default { throw "Bump must be patch, minor, major, or an explicit x.y.z (got '$Bump')." }
}
$tag = "v$new"
Write-Host "Releasing $tag  (was $cur)  [$([IO.Path]::GetFileName($Project))]" -ForegroundColor Cyan

# ---- guards ----
if (git status --porcelain) { throw "Working tree not clean - commit or stash first." }
$branch = git branch --show-current
# origin/HEAD is unset on freshly created repos; fall back to the current branch.
$default = ""
try { $default = (git symbolic-ref refs/remotes/origin/HEAD 2>$null) -replace '.*/', '' } catch { }
if (-not $default) { $default = $branch }
if ($branch -ne $default) { throw "On '$branch' but releases cut from '$default'. Switch branches first." }
git pull -q --ff-only origin $branch
if ($LASTEXITCODE -ne 0) { throw "git pull failed." }
if (git tag -l $tag) { throw "Tag $tag already exists." }

# ---- tests ----
if (-not $NoTest) {
    $tests = Get-ChildItem -Recurse -Filter *.csproj |
        Where-Object { $_.FullName -match '\\tests?\\' -and $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notmatch 'Bench' }
    foreach ($t in $tests) {
        Write-Host "Testing $($t.Name)..." -ForegroundColor Cyan
        dotnet test $t.FullName -c Release --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Tests failed - release aborted." }
    }
}

# ---- bump, commit, tag, push ----
if ($new -ne $cur) {
    $raw = $raw -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$new</Version>"
    [IO.File]::WriteAllText($Project, $raw)
    git add $Project
    git commit -q -m "Release $tag"
    if ($LASTEXITCODE -ne 0) { throw "Commit failed." }
}
git tag $tag
git push -q origin $branch $tag
if ($LASTEXITCODE -ne 0) { throw "Push failed." }

$repo = (git remote get-url origin) -replace '.*github\.com[:/]', '' -replace '\.git$', ''
Write-Host "Pushed $tag - CI is building the release:" -ForegroundColor Green
Write-Host "  https://github.com/$repo/actions"
Write-Host "  https://github.com/$repo/releases/tag/$tag  (when CI finishes)"

if ($Watch) {
    Start-Sleep -Seconds 8
    $runId = gh run list --limit 1 --json databaseId --jq '.[0].databaseId'
    gh run watch $runId --exit-status
}
