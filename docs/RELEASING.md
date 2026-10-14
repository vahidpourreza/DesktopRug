# Publishing a Windows preview

Every completed change set gets a new version and a downloadable GitHub prerelease. A release remains a **preview** while the physical acceptance checks in [MANUAL_TESTS.md](MANUAL_TESTS.md) are open; automated checks do not imply hardware certification.

1. Update `Version` in `src/DesktopRug.App/DesktopRug.App.csproj` and add `docs/releases/<version>.md` with the changes, verification results, and remaining limitations.
2. Complete review and validation, then commit and push that exact source. Never move an existing release tag.
3. From the repository root, create and push its matching tag:

   ```powershell
   [xml]$project = Get-Content -Raw src/DesktopRug.App/DesktopRug.App.csproj
   $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
   git tag -a "v$version" -m "Desktop Rug $version preview"
   git push origin "v$version"
   ```

The [release workflow](../.github/workflows/release.yml) verifies the tag against the application version and requires the version-specific notes. It builds and tests the tagged revision, publishes a self-contained Windows x64 executable, and packages it with its license notices in `DesktopRug-<version>-win-x64.zip`. The workflow creates a draft, uploads and verifies the ZIP's SHA-256, then publishes it as a prerelease. Its Actions summary links to the release and download.

Only the tag workflow has `contents: write`; the ordinary build workflow remains read-only. Publication uses the runner's temporary `GITHUB_TOKEN`, with no repository credentials or machine-specific paths.

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

## Resume a failed publication

Rerun the failed tag workflow from GitHub Actions. It verifies that the remote tag still resolves to the original commit. An existing matching uploaded asset is checked and preserved; a missing asset is uploaded without `--clobber`. A draft is published only after its asset digest is verified. A completed matching preview is an idempotent success.

A conflicting release, moved tag, mismatched digest, or incomplete existing asset fails visibly rather than replacing a download. Inspect the failed run and any draft before taking corrective action. Use a new version for changed source or a replacement build. Release notes contain hidden source-commit and asset-digest markers used by these checks; retain them when editing a published release.
