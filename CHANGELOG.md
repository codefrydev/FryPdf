# Changelog

All notable changes to FryPDF are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/)
(pre-1.0: minor/patch bumps may still include breaking changes).

> This file starts tracking history at `v0.0.7`. Earlier changes are available
> via the [tags](https://github.com/CodeFryDev/FryPDF/tags) and commit history.

## [Unreleased]

### Added
- **Hosts FryWorld.Sdk plugins** next to FryPDF's own, through a second plugin host and a bridge (`Plugins/FryWorldBridge`).
  - **Placement:** their pages, overlays, commands, sidebars, inspectors, dialogs, shortcuts and ribbon tabs appear in FryPDF's UI, and are removed when they unmount.
  - **Services:** they get FryPDF's shell (`IShellService`), settings and theme in FryWorld's shape.
  - **Install location:** they go in the new `fryworld-plugins` folder.
- **FryPdf.PluginSdk 0.2.0:**
  - It references FryWorld.Sdk.
  - Scoped extensions in `PdfEditorApp.Core.Plugins.FryWorldInterop` (`RegisterDocumentImporter`, `RegisterDocumentExporter`, `RegisterTemplate`, `RegisterOcrEngine`, `RegisterDataConnector`, `RegisterCanvasElement`, `IsFryPdfHost`) let FryWorld plugins use FryPDF's PDF features.
  - Its build targets keep `PdfEditorApp.Core.dll` out of FryWorld plugin packages.

### Removed
- **External plugins:** FryPDF no longer ships or offers any of them (Snake, Music Player, Chess, Scratchpad, Image Editor, C# Code Studio, Telemetry HUD, Tic-Tac-Toe). They are FryWorld.Sdk plugins now, and FryPDF runs them from `fryworld-plugins`.
- **Store:** their FryPDF builds are filtered out of the catalog, including catalogs cached before this release, and so are FryWorld-only builds that FryPDF's installer cannot load. Copies users already installed are left in place.
- **Examples and docs:** the `examples/` plugin projects and the Tic-Tac-Toe tutorial (`docs/TIC_TAC_TOE_EXAMPLE.md`) are gone.

## [0.1.2] - 2026-10-12

### Added
- Headless Avalonia UI snapshot and plugin testing suite (`tools/UiSnapshots`).
- Automated off-screen plugin verification and snapshot generation.
- Centrally managed package references for `Avalonia.Headless` and `Avalonia.Skia`.

### Changed
- Improved dynamic plugin studio navigation and viewport rendering in `HomeViewModel`.
- Added unobserved task exception recovery in `Program.cs` to safeguard background tasks.

### Fixed
- Resolved navigation occlusion bug where dynamic full-viewport studio views remained occluded after installing or opening from the Plugins workspace.

### Added
- Sidebar footer with theme toggle and version display.
- Startup loading progress, graceful shutdown persistence, and autosave recovery.
- Global loading progress overlay plugin, service, and UI components.
- Full-screen document loading view with cancellation support.
- Category filtering and search in Settings, with updated search bar visibility logic.
- Plugin installation progress tracking; Marketplace set as the default Settings tab.
- Animated loading spinner asset and `LoadingAnimationControl` for long-running
  operation feedback.
- `LiveNetworkFact` test attribute to isolate network-dependent tests from the
  default test run.

### Changed
- Improved plugin ABI handling and assembly resolution.
- Plugin loading is now protected by a concurrency lock.

### Removed
- Continuous scroll layout mode and its associated rendering logic from the
  viewer components.

[Unreleased]: https://github.com/CodeFryDev/FryPDF/compare/v0.0.7...HEAD
[0.0.7]: https://github.com/CodeFryDev/FryPDF/releases/tag/v0.0.7
