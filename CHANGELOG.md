# Changelog

All notable changes to FryPDF are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/)
(pre-1.0: minor/patch bumps may still include breaking changes).

> This file starts tracking history at `v0.0.7`. Earlier changes are available
> via the [tags](https://github.com/CodeFryDev/FryPDF/tags) and commit history.

## [Unreleased]

## [0.2.0] - 2026-10-30

### Added
- **Interactive Form Filling Engine:**
  - Full living controls on Canvas and Reader mode (`TextBox`, `CheckBox`, `ComboBox`, `RadioButton`, `CalendarDatePicker`, and `Signature`).
  - Scoped AcroForm deconstruction ensuring form fields only appear on their corresponding PDF pages with deterministic disambiguation (`{name}_{page}_{idx}`).
  - Tab and Shift+Tab sequential keyboard navigation in natural document order.
  - Automatic formula recalculation and dependency graph execution for calculated fields.
  - Comprehensive `PdfFormsTool` workspace for tabular inspection, discovery, and batch editing.
- **Dynamic Text Bounding Auto-Expansion:**
  - Automatic vertical height expansion as users type multi-line text.
  - Top-anchored caret initialization preventing lines from scrolling off the top.
- **Hosts FryWorld.Sdk plugins** next to FryPDF's own, through a second plugin host and a bridge (`Plugins/FryWorldBridge`).
  - **Placement:** their pages, overlays, commands, sidebars, inspectors, dialogs, shortcuts and ribbon tabs appear in FryPDF's UI, and are removed when they unmount.
  - **Services:** they get FryPDF's shell (`IShellService`), settings and theme in FryWorld's shape.
  - **Install location:** they go in the new `fryworld-plugins` folder.
- **FryPdf.PluginSdk 0.2.0:**
  - References FryWorld.Sdk.
  - Scoped extensions in `PdfEditorApp.Core.Plugins.FryWorldInterop` (`RegisterDocumentImporter`, `RegisterDocumentExporter`, `RegisterTemplate`, `RegisterOcrEngine`, `RegisterDataConnector`, `RegisterCanvasElement`, `IsFryPdfHost`) let FryWorld plugins use FryPDF's PDF features.
  - Build targets keep `PdfEditorApp.Core.dll` out of FryWorld plugin packages.

### Changed
- **Zero-Clipping Export Pipeline:**
  - Re-architected QuestPDF layer containers with `.MinHeight()` and dynamic text dimension calculation.
  - Automatic font auto-scaling and single-line layout for compact form inputs ($H < 36\text{ pt}$ or `ShowLabel == false`).
  - Specialized vector glyph rendering for checkmarks (✓), radio pills (●), and cursive signatures.
- **Reader-to-Editor Synchronization:**
  - Live synchronization between reader-filled form inputs and the core document model prior to export.

### Fixed
- **Canvas Text In-Place Editing Anomalies:**
  - Eliminated pseudo-HTML tag leakage (`<font=Noto Serif>`, `<size=...>`, `<color=...>`) in `RichTextHelper` and `TextElementViewModel`.
  - Disabled internal canvas `ScrollViewer` vertical and horizontal scrollbars.
  - Resolved export truncation where overflowing text lines failed to print.

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

[Unreleased]: https://github.com/CodeFryDev/FryPDF/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/CodeFryDev/FryPDF/compare/v0.1.3...v0.2.0
[0.1.3]: https://github.com/CodeFryDev/FryPDF/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/CodeFryDev/FryPDF/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/CodeFryDev/FryPDF/compare/v0.1.0...v0.1.1
[0.0.7]: https://github.com/CodeFryDev/FryPDF/releases/tag/v0.0.7
