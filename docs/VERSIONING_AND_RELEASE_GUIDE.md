# FryPDF Versioning & Release Guide

This document provides contributors and maintainers with the exact step-by-step instructions for bumping the product version, releasing new versions of FryPDF, and publishing the `FryPdf.PluginSdk` NuGet package.

---

## 1. Version Architecture & SemVer Principles

FryPDF follows [Semantic Versioning 2.0.0](https://semver.org/) (`MAJOR.MINOR.PATCH`):
- **MAJOR**: Breaking user-facing changes, major platform redesigns, or breaking plugin API changes.
- **MINOR**: New backward-compatible features (e.g. new plugin types, keyboard shortcut managers, viewer capabilities).
- **PATCH**: Backward-compatible bug fixes, performance optimizations, and UI refinements.

### CRITICAL RULE: Plugin Contract ABI Stability (`AssemblyVersion`)
> [!IMPORTANT]
> **Never blindly bump `AssemblyVersion` in `PdfEditorApp.Core.csproj`!**
> 
> `PdfEditorApp.Core` carries a fixed `AssemblyVersion` of `1.0.0.0`. This assembly defines the plugin contract. External plugins compiled against `FryPdf.PluginSdk` bind against this assembly identity.
> 
> If `AssemblyVersion` is changed with every release, previously compiled third-party plugins will fail with `TypeLoadException` or `FileLoadException`.
> 
> - **Bump `<Version>` / `<PackageVersion>`**: Moves with every release (e.g. `0.1.1` $\to$ `0.1.2` or `0.2.0`).
> - **Bump `<AssemblyVersion>`**: Bump **ONLY** when making an intentional, breaking change to the core plugin contract interface.

---

## 2. Step-by-Step Version Bump Checklist

When preparing a new release (e.g., bumping from `0.1.1` to `X.Y.Z`):

### 1. Update Project Definitions
Update the default fallback `<Version>` property in both project files:

- **[`src/PdfEditorApp/PdfEditorApp.csproj`](../src/PdfEditorApp/PdfEditorApp.csproj)**:
  ```xml
  <Version Condition="'$(Version)' == ''">X.Y.Z</Version>
  ```
- **[`src/PdfEditorApp.Core/PdfEditorApp.Core.csproj`](../src/PdfEditorApp.Core/PdfEditorApp.Core.csproj)**:
  ```xml
  <Version Condition="'$(Version)' == ''">X.Y.Z</Version>
  <!-- Keep <AssemblyVersion>1.0.0.0</AssemblyVersion> unchanged unless breaking ABI -->
  ```

### 2. Update Fallback UI & CLI Version Strings
Ensure fallback strings match the new version:

- **[`src/PdfEditorApp/Plugins/Cli/HeadlessCliRunner.cs`](../src/PdfEditorApp/Plugins/Cli/HeadlessCliRunner.cs)**:
  Update the fallback string in `--version` flag handling:
  ```csharp
  var versionString = !string.IsNullOrWhiteSpace(infoVer)
      ? infoVer.Split('+')[0]
      : (assembly.GetName().Version?.ToString(3) ?? "X.Y.Z");
  ```
- **[`src/PdfEditorApp/ViewModels/HomeViewModel.cs`](../src/PdfEditorApp/ViewModels/HomeViewModel.cs)**:
  Update the fallback in `AppVersion`:
  ```csharp
  return "X.Y.Z";
  ```
- **[`src/PdfEditorApp/ViewModels/MainViewModel.Palette.cs`](../src/PdfEditorApp/ViewModels/MainViewModel.Palette.cs)**:
  Update the fallback in `AppVersion`:
  ```csharp
  return "X.Y.Z";
  ```

*(Note: `LicensingPageView.axaml`, `AboutDialog.axaml`, and `HomeView.axaml` bind dynamically to `AppVersionShort` / `AppVersionDisplay` and do not require manual editing).*

### 3. Update Packaging Default Scripts
- **[`packaging/windows/installer.iss`](../packaging/windows/installer.iss)**:
  ```pascal
  #ifndef MyAppVersion
  #define MyAppVersion "X.Y.Z"
  #endif
  ```
- **[`packaging/windows/msix/build-msix.ps1`](../packaging/windows/msix/build-msix.ps1)**:
  ```powershell
  param (
      [string]$Version = "X.Y.Z",
      ...
  ```

### 4. Update SDK Reference in Documentation & Examples
- **[`docs/EXTERNAL_PLUGIN_DEVELOPMENT_GUIDE.md`](EXTERNAL_PLUGIN_DEVELOPMENT_GUIDE.md)**:
  Update the recommended package reference snippet:
  ```xml
  <PackageReference Include="FryPdf.PluginSdk" Version="X.Y.Z" PrivateAssets="all" />
  ```

---

## 3. Local Verification

Before committing and tagging, always perform local verification:

### 1. Verify CLI Version Output
```bash
dotnet run --project src/PdfEditorApp -- --version
```
Expected output:
```text
FryPDF version X.Y.Z (Plugin Architecture Edition)
```

### 2. Run Full Test Suite
Ensure all unit and integration tests pass without error or warnings:
```bash
dotnet test -c release
```

---

## 4. Git Commit, Tag & Push Workflow

### 1. Stage & Commit
```bash
git add -A
git commit -m "chore: bump product version to X.Y.Z for release"
```

### 2. Create Annotated Tag
Release tags must follow the format `vMAJOR.MINOR.PATCH`:
```bash
git tag -a vX.Y.Z -m "Release vX.Y.Z: <brief summary of key features and fixes>"
```

### 3. Push to GitHub
Push both the release commit on `main` and the release tag:
```bash
git push origin main
git push origin vX.Y.Z
```

---

## 5. Automated CI/CD Release Pipeline

Pushing a tag matching `v*` automatically triggers GitHub Actions workflows:

1. **[`.github/workflows/release.yml`](../.github/workflows/release.yml)**:
   - Strips the `v` prefix to extract `X.Y.Z`.
   - Computes the 4-part version (`X.Y.Z.0`) for Windows binaries and MSIX packaging.
   - Runs full test suite on Ubuntu.
   - **Windows Runner**: Builds win-x64 binaries, creates Inno Setup installer (`FryPDF-Setup-X.Y.Z.exe`), builds & signs Windows MSIX package (`FryPDF-X.Y.Z-x64.msix`).
   - **macOS Runner**: Builds osx-arm64 self-contained binary, creates `.app` bundle, ad-hoc codesigns, and packages DMG (`FryPDF-X.Y.Z-arm64.dmg`).
   - **GitHub Release**: Automatically creates a published release with release notes and attaches all installer artifacts.

2. **[`.github/workflows/publish-sdk.yml`](../.github/workflows/publish-sdk.yml)**:
   - Builds and packages `FryPdf.PluginSdk.nupkg` and `.snupkg` with version `X.Y.Z`.
   - Publishes the updated SDK directly to [NuGet.org](https://www.nuget.org/packages/FryPdf.PluginSdk) using OIDC federation.
