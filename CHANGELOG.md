# Changelog

All notable changes to FryPDF are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/)
(pre-1.0: minor/patch bumps may still include breaking changes).

> This file starts tracking history at `v0.0.7`. Earlier changes are available
> via the [tags](https://github.com/CodeFryDev/FryPDF/tags) and commit history.

## [Unreleased]

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
