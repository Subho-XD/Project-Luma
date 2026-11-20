using ProjectLuma.Core.Models;
using ProjectLuma.Core.Services;

namespace ProjectLuma.PackageManager;

/// <summary>
/// PackageManager facade over the shared read-only <see cref="NeuralHubInspector"/>.
/// Distinguishes source-only/installer packages from actual verified NeuralHub v0.9.0 installations.
/// </summary>
public static class NeuralHubDetector
{
    public static NeuralHubDetectionResultInfo Detect(
        string? stagedInstallPath = null,
        NeuralHubProbeOptions? options = null)
        => NeuralHubInspector.Detect(stagedInstallPath, options);

    public static NeuralHubStatusReport InspectFullStatus(
        string? stagedInstallPath = null,
        UserImportedDllRecord? vaultedNrDll = null,
        IEnumerable<DiscoveredGame>? discoveredGames = null,
        NeuralHubProbeOptions? options = null)
        => NeuralHubInspector.InspectFullStatus(stagedInstallPath, vaultedNrDll, discoveredGames, options);

    public static NeuralHubGameIntegrationStatus InspectGameIntegration(
        DiscoveredGame game,
        NeuralHubStatusReport? report)
        => NeuralHubInspector.InspectGameIntegration(game, report);

    public static bool HasRuntimeBinaryPayload(string? directoryPath)
        => NeuralHubInspector.HasRuntimeBinaryPayload(directoryPath);
}
