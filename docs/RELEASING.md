# Publishing a Windows preview

Every completed change set gets a new version and a downloadable GitHub prerelease. A release remains a **preview** while the physical acceptance checks in [MANUAL_TESTS.md](MANUAL_TESTS.md) are open; automated checks do not imply hardware certification.

1. Update `Version` in `src/DesktopRug.App/DesktopRug.App.csproj` and add `docs/releases/<version>.md` with the changes, verification results, and remaining limitations.
2. Complete review and validation, then commit and push that exact source. Wait for the ordinary Windows build, including setup install/upgrade/uninstall smoke tests, to pass before tagging. Never move an existing release tag.
3. From the repository root, create and push its matching tag:

   ```powershell
   [xml]$project = Get-Content -Raw src/DesktopRug.App/DesktopRug.App.csproj
   $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
   git tag -a "v$version" -m "Desktop Rug $version preview"
   git push origin "v$version"
   ```

The [release workflow](../.github/workflows/release.yml) verifies the tag against the application version and requires the version-specific notes. It builds and tests the tagged revision, publishes a self-contained Windows x64 executable, and produces two downloads: `DesktopRug-<version>-win-x64.zip` with the portable app and license notices, and `DesktopRug-<version>-Setup-x64.exe` for per-user installation. An install, upgrade, and uninstall smoke test runs only on the disposable GitHub Actions Windows runner before publication. The workflow creates a draft, uploads and independently verifies both SHA-256 digests, then publishes it as a prerelease. Its Actions summary links to both downloads.

Only the tag workflow has `contents: write`; the ordinary build workflow remains read-only and uploads separate portable/setup Actions artifacts after its installer smoke test. Publication uses the runner's temporary `GITHUB_TOKEN`, with no repository credentials or machine-specific paths.

## Reproduce the portable build locally

Use Windows and .NET SDK **10.0.401**, matching the single-file lock files and release workflow. Confirm it with `dotnet --version`. If another SDK is selected, use a clean checkout and temporarily set its `global.json` SDK version to `10.0.401` with `rollForward: disable`; do not commit this build-only override. The release workflow makes that override in its disposable checkout. Run from the repository root; each command must succeed before continuing:

```powershell
$releaseRoot = (Get-Location).Path
dotnet restore DesktopRug.slnx --locked-mode
dotnet build DesktopRug.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true "-p:PathMap=$releaseRoot=/_/DesktopRug"
dotnet test tests/DesktopRug.Tests/DesktopRug.Tests.csproj -c Release --no-build --no-restore
dotnet restore src/DesktopRug.App/DesktopRug.App.csproj -p:RuntimeIdentifier=win-x64 --locked-mode -p:SelfContained=true -p:PublishSingleFile=true
dotnet publish src/DesktopRug.App/DesktopRug.App.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ContinuousIntegrationBuild=true "-p:PathMap=$releaseRoot=/_/DesktopRug" -o artifacts/manual-release/portable
```

The explicit `RuntimeIdentifier` property on restore selects this repository's `packages.win-x64.lock.json`; the restore command's `-r` option sets a different property and does not select that lock-file path. The executable does not require a separately installed .NET runtime. Keep the `licenses` directory with it. The workflow also includes `THIRD-PARTY-NOTICES.md`, normalizes compilation paths, and uses sorted ZIP entries with the source commit timestamp. Changing the release SDK requires regenerating and reviewing the RID-specific lock files before tagging.

## Compile Windows setup without installing it

Inno Setup 6 is included in the [GitHub Windows runner image](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md). The workflow checks `ISCC.exe` on `PATH`, then its standard Program Files locations, and fails explicitly if unavailable. It does not download an unpinned compiler. With Inno Setup 6 installed locally, compile only:

```powershell
[xml]$project = Get-Content -Raw src/DesktopRug.App/DesktopRug.App.csproj
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$portable = (Resolve-Path artifacts/manual-release/portable).Path
Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination $portable
$destination = [IO.Path]::GetFullPath('artifacts/manual-release')
$compiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
& $compiler "/DAppVersion=$version" "/DSourceDir=$portable" "/DOutputDir=$destination" packaging/desktop-rug.iss
```

Compilation creates `DesktopRug-<version>-Setup-x64.exe` without running it. Setup is per-user, requires no administrator rights, and does not automatically launch the app or enable sign-in startup. The CI smoke script rejects local execution; do not spoof its GitHub Actions environment guards to run it on a working machine.

The initial upgrade check is an in-place reinstall of the same installer version, verifying preservation of settings and unrelated files. It does not claim migration coverage from a previously released installer binary.

## Resume a failed publication

Rerun the failed tag workflow from GitHub Actions. It verifies that the remote tag still resolves to the original commit. Existing ZIP/setup assets are independently checked and preserved; only missing assets are uploaded, without `--clobber`. If a draft contains one completed download, rebuilding the missing package may update only that package's recorded digest; it never changes the existing download or its digest. A draft is published only after both assets are verified. A completed matching preview is an idempotent success.

GitHub may briefly return stale state after a write. The workflow allows six read-only readiness attempts after draft creation, notes update, upload verification, and publication, with delays of 1, 2, 4, 8, and 8 seconds between attempts. Once discovered, the release is read by its ID. Missing/processing asset data can become ready during that wait; authentication failures, changed source, and conflicting digests fail immediately. Create, upload, and publish commands are never repeated by the readiness loop. A timeout reports the phase and retains the draft and assets for a later rerun.

A conflicting release, moved tag, mismatched digest, or incomplete existing asset fails visibly rather than replacing a download. Inspect the failed run and any draft before taking corrective action. Use a new version for changed source or a replacement build. Release notes contain a hidden source-commit marker and filename-specific asset-digest markers used by these checks; retain them when editing a published release.
