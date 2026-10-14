# Third-party notices

The application and supplied rug-reference images are distinct from these dependencies. Dependency license notices are retained in `licenses/` and copied beside portable builds. Rug-source handling is documented in `docs/MATERIALS.md`.

| Component | Version used for this preview | Notice |
| --- | --- | --- |
| SkiaSharp and its Windows native assets | 4.153.1 | `licenses/skiasharp-license.txt`, `licenses/skia-third-party-notices.txt` |
| .NET runtime | 10.0.12 | `licenses/dotnet-license.txt`, `licenses/dotnet-third-party-notices.txt` |
| .NET Windows Desktop runtime | 10.0.12 | `licenses/windowsdesktop-license.txt` |

The build/test-only xUnit and Microsoft.NET.Test.Sdk packages retain their own package licenses and are not shipped as application features. Runtime-specific NuGet lock files are separate from ordinary build lock files so publishing does not invalidate a developer's locked restore.
