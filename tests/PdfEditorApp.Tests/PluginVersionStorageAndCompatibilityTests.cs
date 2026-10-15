using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using PdfEditorApp.Core.Plugins.Manifests;
using PdfEditorApp.Core.Plugins.Marketplace;
using PdfEditorApp.Plugins.Loader;
using PdfEditorApp.ViewModels;
using Xunit;

namespace PdfEditorApp.Tests;

public class PluginVersionStorageAndCompatibilityTests
{
    [Fact]
    public void CompatibilityChecker_ValidatesSemVerRangesCorrectly()
    {
        // Compatible min host version
        var v1 = new MarketplacePluginVersion
        {
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            MaxHostVersion = "",
            TargetFramework = "net10.0"
        };
        var check1 = PluginCompatibilityChecker.CheckCompatibility(v1, hostVersionStr: "1.0.0");
        Assert.True(check1.IsCompatible);
        Assert.Contains("Compatible", check1.Message);

        // Incompatible min host version (requires host 2.0.0+, but running 1.0.0)
        var v2 = new MarketplacePluginVersion
        {
            Version = "2.0.0",
            MinHostVersion = "2.0.0",
            MaxHostVersion = "",
            TargetFramework = "net10.0"
        };
        var check2 = PluginCompatibilityChecker.CheckCompatibility(v2, hostVersionStr: "1.0.0");
        Assert.False(check2.IsCompatible);
        Assert.Contains("Requires FryPDF v2.0", check2.Message);

        // Incompatible max host version (supports up to host 0.9.0, running 1.0.0)
        var v3 = new MarketplacePluginVersion
        {
            Version = "0.9.0",
            MinHostVersion = "0.5.0",
            MaxHostVersion = "0.9.0",
            TargetFramework = "net10.0"
        };
        var check3 = PluginCompatibilityChecker.CheckCompatibility(v3, hostVersionStr: "1.0.0");
        Assert.False(check3.IsCompatible);
        Assert.Contains("Compatible up to FryPDF v0.9", check3.Message);
    }

    [Fact]
    public void CompatibilityChecker_ValidatesTargetFrameworkMonikers()
    {
        // Compatible .NET versions (Host is net10.0)
        Assert.True(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "net10.0"));
        Assert.True(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "net9.0"));
        Assert.True(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "net8.0"));
        Assert.True(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "netstandard2.0"));
        Assert.True(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "netstandard2.1"));

        // Incompatible future .NET versions
        Assert.False(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "net11.0"));
        Assert.False(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "net12.0"));

        // Incompatible legacy .NET Framework
        Assert.False(PluginCompatibilityChecker.IsTfmCompatible("net10.0", "net48"));
    }

    [Fact]
    public void PluginIdValidator_ResolvesVersionedInstallDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"fry_test_{Guid.NewGuid():N}");
        try
        {
            // Valid ID and Version
            var dir = PluginIdValidator.ResolveInstallDirectory(tempRoot, "com.frypdf.plugin.sample", "1.2.0", "test context");
            Assert.Equal(Path.Combine(tempRoot, "com.frypdf.plugin.sample", "1.2.0"), dir);

            // Strips leading 'v'
            var dir2 = PluginIdValidator.ResolveInstallDirectory(tempRoot, "com.frypdf.plugin.sample", "v1.2.0", "test context");
            Assert.Equal(Path.Combine(tempRoot, "com.frypdf.plugin.sample", "1.2.0"), dir2);

            // Null/empty version falls back to unversioned root
            var dirUnversioned = PluginIdValidator.ResolveInstallDirectory(tempRoot, "com.frypdf.plugin.sample", null, "test context");
            Assert.Equal(Path.Combine(tempRoot, "com.frypdf.plugin.sample"), dirUnversioned);

            // Rejects path traversal in version
            Assert.Throws<ArgumentException>(() =>
                PluginIdValidator.ResolveInstallDirectory(tempRoot, "com.frypdf.plugin.sample", "../../../etc", "test context"));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void PluginAssemblyLoader_ResolvesLegacyFlatDirectory()
    {
        var tempPluginDir = Path.Combine(Path.GetTempPath(), $"fry_flat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPluginDir);
        try
        {
            var dummyDll = Path.Combine(tempPluginDir, "MyPlugin.dll");
            File.WriteAllBytes(dummyDll, new byte[] { 0x4D, 0x5A }); // MZ header dummy

            var resolved = PluginAssemblyLoader.ResolvePluginDirectoryEntryDll(tempPluginDir, targetVersion: null);
            Assert.NotNull(resolved);
            Assert.Equal(dummyDll, resolved);
        }
        finally
        {
            if (Directory.Exists(tempPluginDir)) Directory.Delete(tempPluginDir, true);
        }
    }

    [Fact]
    public void PluginAssemblyLoader_DiscoversHighestVersionWhenNoActiveVersionSpecified()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"fry_multi_root_{Guid.NewGuid():N}");
        var pluginId = "com.frypdf.plugin.multi";
        var tempPluginDir = Path.Combine(tempRoot, pluginId);
        Directory.CreateDirectory(tempPluginDir);
        try
        {
            var v1Dir = Path.Combine(tempPluginDir, "1.0.0");
            var v2Dir = Path.Combine(tempPluginDir, "1.2.0");
            var v3Dir = Path.Combine(tempPluginDir, "1.1.0");

            Directory.CreateDirectory(v1Dir);
            Directory.CreateDirectory(v2Dir);
            Directory.CreateDirectory(v3Dir);

            var dll1 = Path.Combine(v1Dir, "TestPlugin.dll");
            var dll2 = Path.Combine(v2Dir, "TestPlugin.dll");
            var dll3 = Path.Combine(v3Dir, "TestPlugin.dll");

            File.WriteAllBytes(dll1, new byte[] { 0x4D, 0x5A });
            File.WriteAllBytes(dll2, new byte[] { 0x4D, 0x5A });
            File.WriteAllBytes(dll3, new byte[] { 0x4D, 0x5A });

            // Without active version: picks 1.2.0 (highest)
            var resolved = PluginAssemblyLoader.ResolvePluginDirectoryEntryDll(tempPluginDir, targetVersion: null);
            Assert.NotNull(resolved);
            Assert.Equal(dll2, resolved);

            // Versions on disk should be sorted descending: 1.2.0, 1.1.0, 1.0.0
            var versions = PluginAssemblyLoader.GetInstalledVersionsOnDisk(tempRoot, pluginId);
            Assert.Equal(3, versions.Count);
            Assert.Equal("1.2.0", versions[0]);
            Assert.Equal("1.1.0", versions[1]);
            Assert.Equal("1.0.0", versions[2]);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void PluginAssemblyLoader_ResolvesSpecifiedActiveVersion()
    {
        var tempPluginDir = Path.Combine(Path.GetTempPath(), $"fry_multi_act_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPluginDir);
        try
        {
            var v1Dir = Path.Combine(tempPluginDir, "1.0.0");
            var v2Dir = Path.Combine(tempPluginDir, "1.2.0");

            Directory.CreateDirectory(v1Dir);
            Directory.CreateDirectory(v2Dir);

            var dll1 = Path.Combine(v1Dir, "TestPlugin.dll");
            var dll2 = Path.Combine(v2Dir, "TestPlugin.dll");

            File.WriteAllBytes(dll1, new byte[] { 0x4D, 0x5A });
            File.WriteAllBytes(dll2, new byte[] { 0x4D, 0x5A });

            // Target specifically 1.0.0 (even though 1.2.0 is higher)
            var resolved = PluginAssemblyLoader.ResolvePluginDirectoryEntryDll(tempPluginDir, targetVersion: "1.0.0");
            Assert.NotNull(resolved);
            Assert.Equal(dll1, resolved);
        }
        finally
        {
            if (Directory.Exists(tempPluginDir)) Directory.Delete(tempPluginDir, true);
        }
    }

    [Fact]
    public void FryPluginPackageLoader_UnpacksIntoVersionSubdirectoryWithoutDeletingSiblingVersions()
    {
        var tempBaseDir = Path.Combine(Path.GetTempPath(), $"fry_unpack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBaseDir);
        try
        {
            var pluginId = "com.frypdf.plugin.test";

            // Create a fake .fryplugin zip for v1.0.0
            var pkgV1 = Path.Combine(tempBaseDir, "TestV1.fryplugin");
            CreateFakeFryPlugin(pkgV1, pluginId, "1.0.0");

            // Create a fake .fryplugin zip for v1.1.0
            var pkgV2 = Path.Combine(tempBaseDir, "TestV2.fryplugin");
            CreateFakeFryPlugin(pkgV2, pluginId, "1.1.0");

            // Unpack v1.0.0
            var manifestV1 = FryPluginPackageLoader.UnpackPackage(pkgV1, tempBaseDir, "1.0.0");
            Assert.NotNull(manifestV1);
            Assert.Equal("1.0.0", manifestV1.Version);
            Assert.True(Directory.Exists(Path.Combine(tempBaseDir, pluginId, "1.0.0")));

            // Unpack v1.1.0
            var manifestV2 = FryPluginPackageLoader.UnpackPackage(pkgV2, tempBaseDir, "1.1.0");
            Assert.NotNull(manifestV2);
            Assert.Equal("1.1.0", manifestV2.Version);
            Assert.True(Directory.Exists(Path.Combine(tempBaseDir, pluginId, "1.1.0")));

            // Sibling v1.0.0 must still exist intact
            Assert.True(Directory.Exists(Path.Combine(tempBaseDir, pluginId, "1.0.0")), "v1.0.0 must not be deleted when installing v1.1.0");
        }
        finally
        {
            if (Directory.Exists(tempBaseDir)) Directory.Delete(tempBaseDir, true);
        }
    }

    [Fact]
    public void DetailViewModel_UpdateVersionState_CorrectlyCalculatesUpgradeAndRollback()
    {
        var vm = new PluginsManagerDetailViewModel
        {
            Id = "com.frypdf.test",
            Name = "Test Plugin",
            Version = "1.1.0",
            IsInstalled = true,
            IsActive = true,
            ActiveInstalledVersion = "1.1.0"
        };

        var ver1 = new MarketplacePluginVersion { Version = "1.0.0", IsInstalled = true };
        var ver2 = new MarketplacePluginVersion { Version = "1.1.0", IsInstalled = true, IsActive = true };
        var ver3 = new MarketplacePluginVersion { Version = "1.2.0", IsInstalled = false };

        vm.AvailableVersions.Add(ver3);
        vm.AvailableVersions.Add(ver2);
        vm.AvailableVersions.Add(ver1);

        // When viewing active version 1.1.0
        vm.SelectedVersion = ver2;
        Assert.True(vm.IsSelectedVersionActive);
        Assert.False(vm.IsUpgradeAvailable);
        Assert.False(vm.IsRollbackAvailable);
        Assert.Equal("Installed", vm.VersionActionText);
        Assert.False(vm.CanShowVersionAction);

        // When viewing older cached version 1.0.0 (Switch)
        vm.SelectedVersion = ver1;
        Assert.False(vm.IsSelectedVersionActive);
        Assert.True(vm.IsSelectedVersionInstalled);
        Assert.Equal("Switch to v1.0.0", vm.VersionActionText);
        Assert.True(vm.CanShowVersionAction);

        // When viewing newer uninstalled version 1.2.0 (Update)
        vm.SelectedVersion = ver3;
        Assert.False(vm.IsSelectedVersionActive);
        Assert.False(vm.IsSelectedVersionInstalled);
        Assert.True(vm.IsUpgradeAvailable);
        Assert.False(vm.IsRollbackAvailable);
        Assert.Equal("Update to v1.2.0", vm.VersionActionText);
        Assert.True(vm.CanShowVersionAction);
    }

    private static void CreateFakeFryPlugin(string zipPath, string id, string version)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifestEntry = zip.CreateEntry("plugin.json");
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            var manifest = new
            {
                id = id,
                name = "Test Plugin",
                version = version,
                description = "Test Description",
                entryPoint = "TestPlugin.dll",
                targetFramework = "net10.0"
            };
            writer.Write(JsonSerializer.Serialize(manifest));
        }

        var dllEntry = zip.CreateEntry("TestPlugin.dll");
        using (var stream = dllEntry.Open())
        {
            stream.Write(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        }
    }
}
