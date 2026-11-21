using System.IO;
using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;

namespace ProjectLuma.UI.ViewModels;

public sealed class GameCardViewModel : ObservableObject
{
    private readonly Action<GameCardViewModel>? _onProfileEdited;
    private bool _suppressEditNotification;

    public DiscoveredGame Game { get; }
    public GameEnhancementProfile Profile { get; set; }

    public GameCardViewModel(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        Action<GameCardViewModel>? onProfileEdited = null)
    {
        Game = game;
        Profile = profile;
        _onProfileEdited = onProfileEdited;
    }

    public string Id => Game.Id;
    public string Title => Game.Title;
    public string ExecutableName => Path.GetFileName(Game.ExecutablePath);
    public string LauncherBadge => Game.Launcher switch
    {
        GameLauncherType.Steam => "Steam",
        GameLauncherType.EpicGames => "Epic Games",
        GameLauncherType.GogGalaxy => "GOG Galaxy",
        GameLauncherType.UbisoftConnect => "Ubisoft Connect",
        GameLauncherType.EaApp => "EA App",
        GameLauncherType.BattleNet => "Battle.net",
        GameLauncherType.Xbox => "Xbox",
        _ => "Manual"
    };
    public string ApiBadge => Game.GraphicsApi switch
    {
        GraphicsApi.DirectX12 => "DirectX 12",
        GraphicsApi.DirectX11 => "DirectX 11",
        GraphicsApi.Vulkan => "Vulkan",
        _ => "Auto API"
    };
    public string EngineName => Game.EngineName;

    public string CoverImagePath => Game.CoverImagePath;
    public string HeroImagePath => Game.HeroImagePath;
    public string LogoImagePath => Game.LogoImagePath;
    public bool HasLogoImage => !string.IsNullOrWhiteSpace(Game.LogoImagePath) && File.Exists(Game.LogoImagePath);
    public bool IsGeneratedFallbackArt => Game.ArtworkSource == ArtworkSourceKind.GeneratedFallback;
    public FriendlyGameStatus FriendlyStatus => Game.FriendlyStatus;
    public string FriendlyStatusText => Game.FriendlyStatusText;
    public string RecommendationSummary => Game.RecommendationSummary;
    public bool IsAvailable => Game.IsAvailable;
    public bool IsManuallyAdded => Game.IsManuallyAdded;
    public string AvailabilityReason => Game.AvailabilityReason;

    private GameLaunchPhase _launchPhase = GameLaunchPhase.Idle;

    public GameLaunchPhase LaunchPhase
    {
        get => _launchPhase;
        set
        {
            if (SetProperty(ref _launchPhase, value))
                OnPropertyChanged(nameof(PlayButtonLabel));
        }
    }

    public string PlayButtonLabel => LaunchPhase switch
    {
        GameLaunchPhase.Starting or GameLaunchPhase.PreparingEnhancements => "Preparing...",
        GameLaunchPhase.Launching => "Launching...",
        GameLaunchPhase.LauncherInvoked => "Launcher Opened",
        GameLaunchPhase.Running => "Running",
        _ => "Play"
    };

    public CapabilityState NeuralState => Profile.NeuralRenderingState;
    public CapabilityState SuperResState => Profile.SuperResolutionState;
    public CapabilityState FrameGenState => Profile.FrameGenerationState;
    public CapabilityState FrameGenBadgeState => Game.NativeFrameGenSupported
        ? CapabilityState.SupportedWithLimitations
        : (Profile.SelectedFrameGenEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
            ? CapabilityState.Unsupported
            : Profile.FrameGenerationState);
    public CapabilityState RayReconState => Profile.RayReconstructionState;

    public bool CanToggleNeural =>
        (Profile.NeuralRenderingState is CapabilityState.Supported or CapabilityState.Experimental) &&
        (Profile.NeuralActivationState is NeuralRenderingActivationState.ReadyToConfigure or NeuralRenderingActivationState.Active or NeuralRenderingActivationState.Available);

    public bool ShowFindRequiredComponents =>
        !CanToggleNeural &&
        Game.AntiCheatStatus is not (AntiCheatStatus.DetectedBlocked or AntiCheatStatus.WindowsAppsProtected) &&
        Game.Is64Bit;

    public bool CanToggleSuperRes => Profile.SuperResolutionState is CapabilityState.Supported or CapabilityState.SupportedWithLimitations;

    public bool CanToggleFrameGen =>
        !Game.NativeFrameGenSupported &&
        Game.Is64Bit &&
        Game.GraphicsApi is GraphicsApi.DirectX12 or GraphicsApi.Unknown &&
        Profile.FrameGenerationState is CapabilityState.Supported or CapabilityState.SupportedWithLimitations &&
        !Profile.SelectedFrameGenEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) &&
        !Profile.SelectedFrameGenEngine.Contains("AFMF", StringComparison.OrdinalIgnoreCase);

    public bool CanToggleRayRecon => Profile.RayReconstructionState == CapabilityState.Supported && Game.NativeRayReconstructionSupported;

    public string NeuralUnavailableReason => CanToggleNeural
        ? string.Empty
        : (!string.IsNullOrWhiteSpace(Profile.NeuralActivationExplanation) ? Profile.NeuralActivationExplanation : Profile.NeuralRenderingReason);
    public string SuperResUnavailableReason => CanToggleSuperRes ? string.Empty : Profile.SuperResolutionReason;
    public string FrameGenUnavailableReason
    {
        get
        {
            if (CanToggleFrameGen) return string.Empty;
            if (Game.NativeFrameGenSupported)
                return string.IsNullOrWhiteSpace(Profile.NativeFrameGenGuide)
                    ? Profile.DoubleFgWarning
                    : $"{Profile.DoubleFgWarning} {Profile.NativeFrameGenGuide}";
            if (Profile.SelectedFrameGenEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
                return "DLSS Frame Generation via DLSS-NR-on-AMD is unavailable on RX 6000 (RDNA 2) per v0.9.0 release notes. Choose AMD FSR Frame Generation 4.0.2 or OptiScaler OptiFG (DX12 + HUDfix).";
            return Profile.FrameGenerationReason;
        }
    }
    public string RayReconUnavailableReason => CanToggleRayRecon ? string.Empty : Profile.RayReconstructionReason;

    // Two-way editable per-game enhancement settings (automatically mark Custom & persist to SQLite)
    public bool EnableNeuralRendering
    {
        get => Profile.EnableNeuralRendering;
        set
        {
            if (Profile.EnableNeuralRendering == value) return;
            if (value && !CanToggleNeural) return;
            Profile.EnableNeuralRendering = value;
            CommitSettingEdit(markCustom: true);
        }
    }

    public bool EnableSuperResolution
    {
        get => Profile.EnableSuperResolution;
        set
        {
            if (Profile.EnableSuperResolution == value) return;
            Profile.EnableSuperResolution = value;
            CommitSettingEdit(markCustom: true);
        }
    }

    public bool EnableFrameGeneration
    {
        get => Profile.EnableFrameGeneration;
        set
        {
            if (Profile.EnableFrameGeneration == value) return;
            if (value && !CanToggleFrameGen) return;
            Profile.EnableFrameGeneration = value;
            CommitSettingEdit(markCustom: true);
        }
    }

    public bool EnableRayReconstruction
    {
        get => Profile.EnableRayReconstruction;
        set
        {
            if (Profile.EnableRayReconstruction == value) return;
            if (value && !CanToggleRayRecon) return;
            Profile.EnableRayReconstruction = value;
            CommitSettingEdit(markCustom: true);
        }
    }

    public string SelectedUpscalerEngine
    {
        get => Profile.SelectedUpscalerEngine;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Profile.SelectedUpscalerEngine, value, StringComparison.Ordinal)) return;
            Profile.SelectedUpscalerEngine = value;
            CommitSettingEdit(markCustom: true);
            OnPropertyChanged(nameof(SelectedUpscalerEngine));
        }
    }

    public UpscalingQualityPreset SelectedUpscalingPreset
    {
        get => Profile.SelectedUpscalingPreset;
        set
        {
            if (Profile.SelectedUpscalingPreset == value) return;
            Profile.SelectedUpscalingPreset = value;
            CommitSettingEdit(markCustom: true);
            OnPropertyChanged(nameof(SelectedUpscalingPreset));
        }
    }

    public string SelectedFrameGenEngine
    {
        get => Profile.SelectedFrameGenEngine;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Profile.SelectedFrameGenEngine, value, StringComparison.Ordinal)) return;
            Profile.SelectedFrameGenEngine = value;
            if (!CanToggleFrameGen)
                Profile.EnableFrameGeneration = false;
            CommitSettingEdit(markCustom: true);
            OnPropertyChanged(nameof(SelectedFrameGenEngine));
        }
    }

    public string ProxyDllName
    {
        get => Profile.ProxyDllName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(Profile.ProxyDllName, value, StringComparison.OrdinalIgnoreCase)) return;
            Profile.ProxyDllName = value;
            CommitSettingEdit(markCustom: true);
            OnPropertyChanged(nameof(ProxyDllName));
        }
    }

    public bool ShowFsr4Watermark
    {
        get => Profile.ShowFsr4Watermark;
        set
        {
            if (Profile.ShowFsr4Watermark == value) return;
            Profile.ShowFsr4Watermark = value;
            CommitSettingEdit(markCustom: false);
            OnPropertyChanged(nameof(ShowFsr4Watermark));
        }
    }

    public bool EnableHudFix
    {
        get => Profile.EnableHudFix;
        set
        {
            if (Profile.EnableHudFix == value) return;
            Profile.EnableHudFix = value;
            CommitSettingEdit(markCustom: false);
            OnPropertyChanged(nameof(EnableHudFix));
        }
    }

    public bool EnableAntiLag2
    {
        get => Profile.EnableAntiLag2;
        set
        {
            if (Profile.EnableAntiLag2 == value) return;
            Profile.EnableAntiLag2 = value;
            CommitSettingEdit(markCustom: false);
            OnPropertyChanged(nameof(EnableAntiLag2));
        }
    }

    public bool OfflineAntiCheatConfirmed
    {
        get => Profile.OfflineAntiCheatConfirmed;
        set
        {
            if (Profile.OfflineAntiCheatConfirmed == value) return;
            Profile.OfflineAntiCheatConfirmed = value;
            CommitSettingEdit(markCustom: false);
            OnPropertyChanged(nameof(OfflineAntiCheatConfirmed));
        }
    }

    private void CommitSettingEdit(bool markCustom)
    {
        if (_suppressEditNotification) return;
        if (markCustom)
        {
            Profile.SelectedPreset = EnhancementProfilePreset.Custom;
            Profile.PresetSummary = "Customize individual supported visual enhancements and fine-tune quality settings.";
        }
        CapabilityEngine.PopulateFriendlyStatuses(Game, Profile);
        NotifyStatusUpdated();
        _onProfileEdited?.Invoke(this);
    }

    public bool IsInstalled => !string.IsNullOrWhiteSpace(Game.InstalledBackendId);
    public string InstalledStatusText => IsInstalled
        ? $"Enhanced ({Profile.SelectedPreset})"
        : "Not yet enhanced";

    public bool HasAntiCheatWarning => Game.AntiCheatStatus != AntiCheatStatus.None;
    public string NativeUpscalersSummary
    {
        get
        {
            var list = new List<string>();
            if (Game.NativeDlssSupported) list.Add("DLSS");
            if (Game.NativeFsrSupported) list.Add("FSR");
            if (Game.NativeXeSsSupported) list.Add("XeSS");
            if (Game.NativeFrameGenSupported) list.Add("Built-in Frame Generation");
            return list.Count > 0 ? string.Join(" · ", list) : "Standard rendering detected";
        }
    }

    public string Monogram => string.IsNullOrWhiteSpace(Title)
        ? "GM"
        : string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public void NotifyArtworkUpdated()
    {
        OnPropertyChanged(nameof(CoverImagePath));
        OnPropertyChanged(nameof(HeroImagePath));
        OnPropertyChanged(nameof(LogoImagePath));
        OnPropertyChanged(nameof(HasLogoImage));
        OnPropertyChanged(nameof(IsGeneratedFallbackArt));
    }

    public void NotifyStatusUpdated()
    {
        _suppressEditNotification = true;
        try
        {
            OnPropertyChanged(nameof(FriendlyStatus));
            OnPropertyChanged(nameof(FriendlyStatusText));
            OnPropertyChanged(nameof(RecommendationSummary));
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(AvailabilityReason));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(InstalledStatusText));
            OnPropertyChanged(nameof(NeuralState));
            OnPropertyChanged(nameof(SuperResState));
            OnPropertyChanged(nameof(FrameGenState));
            OnPropertyChanged(nameof(FrameGenBadgeState));
            OnPropertyChanged(nameof(RayReconState));
            OnPropertyChanged(nameof(CanToggleNeural));
            OnPropertyChanged(nameof(ShowFindRequiredComponents));
            OnPropertyChanged(nameof(CanToggleSuperRes));
            OnPropertyChanged(nameof(CanToggleFrameGen));
            OnPropertyChanged(nameof(CanToggleRayRecon));
            OnPropertyChanged(nameof(NeuralUnavailableReason));
            OnPropertyChanged(nameof(SuperResUnavailableReason));
            OnPropertyChanged(nameof(FrameGenUnavailableReason));
            OnPropertyChanged(nameof(RayReconUnavailableReason));
            OnPropertyChanged(nameof(EnableNeuralRendering));
            OnPropertyChanged(nameof(EnableSuperResolution));
            OnPropertyChanged(nameof(EnableFrameGeneration));
            OnPropertyChanged(nameof(EnableRayReconstruction));
            OnPropertyChanged(nameof(SelectedUpscalerEngine));
            OnPropertyChanged(nameof(SelectedUpscalingPreset));
            OnPropertyChanged(nameof(SelectedFrameGenEngine));
            OnPropertyChanged(nameof(ProxyDllName));
            OnPropertyChanged(nameof(ShowFsr4Watermark));
            OnPropertyChanged(nameof(EnableHudFix));
            OnPropertyChanged(nameof(EnableAntiLag2));
            OnPropertyChanged(nameof(OfflineAntiCheatConfirmed));
            OnPropertyChanged(nameof(Profile));
        }
        finally
        {
            _suppressEditNotification = false;
        }
    }
}

/// <summary>Virtualized horizontal row containing up to 4–5 portrait game cards for smooth 1,000+ game scrolling.</summary>
public sealed class GameCoverRow
{
    public List<GameCardViewModel> Cards { get; }
    public GameCoverRow(List<GameCardViewModel> cards) => Cards = cards;
}
