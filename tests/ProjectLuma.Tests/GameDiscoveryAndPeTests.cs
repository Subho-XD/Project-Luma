using ProjectLuma.Core.Enums;
using ProjectLuma.GameDiscovery;
using ProjectLuma.Logging;
using Xunit;

namespace ProjectLuma.Tests;

public sealed class GameDiscoveryAndPeTests
{
    [Fact]
    public async Task VdfParser_Parses_LibraryFolders_And_Rejects_PathTraversal_In_Acf()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaSteamTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string steamApps = Path.Combine(tempRoot, "steamapps");
            string common = Path.Combine(steamApps, "common", "TestGame");
            Directory.CreateDirectory(common);

            // Copy a real 64-bit PE executable for realistic PE inspection
            string sampleExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            File.Copy(sampleExe, Path.Combine(common, "TestGame.exe"));

            File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), $$"""
                "libraryfolders"
                {
                    "0"
                    {
                        "path" "{{tempRoot.Replace("\\", "\\\\")}}"
                    }
                }
                """);

            File.WriteAllText(Path.Combine(steamApps, "appmanifest_999001.acf"), """
                "AppState"
                {
                    "appid" "999001"
                    "name" "TestGame"
                    "installdir" "TestGame"
                }
                """);

            // Malicious path-traversal manifest must be ignored safely
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_999002.acf"), """
                "AppState"
                {
                    "appid" "999002"
                    "name" "EvilGame"
                    "installdir" "..\\..\\Windows"
                }
                """);

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var scanner = new MultiStoreGameScanner(logger, new PeAnalyzer());
            var found = await scanner.ScanWithOptionsAsync(
                Array.Empty<string>(),
                new MultiStoreGameScanner.ScanOptions { SteamRootOverride = tempRoot, DisableRegistry = true });

            Assert.Single(found);
            Assert.Equal("TestGame", found[0].Title);
            Assert.True(found[0].Is64Bit);
            Assert.Equal(GameLauncherType.Steam, found[0].Launcher);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void PeAnalyzer_Inspects_Real_64Bit_Windows_Binary()
    {
        var analyzer = new PeAnalyzer();
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var res = analyzer.InspectFile(notepad);

        Assert.NotNull(res);
        Assert.True(res!.Is64Bit);
        Assert.False(res.IsDll);
        Assert.NotEmpty(res.ImportedDlls);
    }

    [Fact]
    public void AntiCheatDetector_Identifies_EAC_BattlEye_And_WindowsApps()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaAcTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string eacGame = Path.Combine(tempRoot, "GameA");
            Directory.CreateDirectory(Path.Combine(eacGame, "EasyAntiCheat"));
            var (st1, _) = AntiCheatDetector.Evaluate(eacGame, eacGame);
            Assert.Equal(AntiCheatStatus.DetectedBlocked, st1);

            string beGame = Path.Combine(tempRoot, "GameB");
            Directory.CreateDirectory(beGame);
            File.WriteAllText(Path.Combine(beGame, "BEClient_x64.dll"), "stub");
            var (st2, _) = AntiCheatDetector.Evaluate(beGame, beGame);
            Assert.Equal(AntiCheatStatus.DetectedBlocked, st2);

            var (st3, _) = AntiCheatDetector.Evaluate(@"C:\Program Files\WindowsApps\SomeGame", @"C:\Program Files\WindowsApps\SomeGame");
            Assert.Equal(AntiCheatStatus.WindowsAppsProtected, st3);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }
}
