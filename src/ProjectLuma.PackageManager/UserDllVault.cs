using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.PackageManager;

/// <summary>Validates and stores user-supplied proprietary NVIDIA NGX DLLs locally without ever executing, downloading, or bundling them.</summary>
public sealed class UserDllVault : IUserDllVault
{
    private readonly ICompatibilityRepository _repo;
    private readonly ILumaLogger _logger;
    private readonly string _requirementsManifestPath;
    private readonly Func<string, Version?>? _versionOverride;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class PeValidationInfo
    {
        public bool IsReadable { get; set; }
        public bool IsPeValid { get; set; }
        public bool IsDll { get; set; }
        public bool Is64Bit { get; set; }
        public bool IsCorrupted { get; set; }
        public string ArchitectureText { get; set; } = "Unknown";
        public long FileSizeBytes { get; set; }
        public string FailureDetail { get; set; } = string.Empty;
    }

    public string VaultDirectory { get; }
    public string NeuralRenderingVaultDirectory => Path.Combine(VaultDirectory, "NVIDIA", "DLSS-NR");

    public UserDllVault(
        ICompatibilityRepository repo,
        ILumaLogger logger,
        string? vaultDirectory = null,
        string? requirementsManifestPath = null,
        Func<string, Version?>? versionOverride = null)
    {
        _repo = repo;
        _logger = logger;
        _versionOverride = versionOverride;
        VaultDirectory = string.IsNullOrWhiteSpace(vaultDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectLuma", "UserComponents")
            : vaultDirectory;
        Directory.CreateDirectory(VaultDirectory);
        Directory.CreateDirectory(NeuralRenderingVaultDirectory);
        _requirementsManifestPath = ResolveRequirementsManifestPath(requirementsManifestPath);
    }

    public NeuralRenderingRequirementsManifest GetNeuralRequirementsManifest()
    {
        try
        {
            if (File.Exists(_requirementsManifestPath))
            {
                string json = File.ReadAllText(_requirementsManifestPath);
                var loaded = JsonSerializer.Deserialize<NeuralRenderingRequirementsManifest>(json, JsonOptions);
                if (loaded != null && loaded.Models.Count > 0)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("UserDllVault", $"Could not parse neural_rendering_requirements.json; using verified v0.9.0 defaults: {ex.Message}");
        }

        return new NeuralRenderingRequirementsManifest
        {
            Models = new Dictionary<string, NeuralModelRequirement>(StringComparer.OrdinalIgnoreCase)
            {
                ["CC"] = new() { ModelKey = "CC", DisplayName = "CC Model (Fastest)", RequiredDll = "nvngx_dlssnr.dll", MinimumVersion = "310.9.16.0" },
                ["HT"] = new() { ModelKey = "HT", DisplayName = "HT Model (Standard)", RequiredDll = "nvngx_dlssnr.dll", MinimumVersion = "310.8.0.0" }
            },
            OptionalCompanionDlls =
            {
                new() { FileName = "nvngx_dlss.dll", DllKind = "nvngx_dlss", Feature = "SuperResolution", MinimumVersion = "3.5.0.0", Rdna2Supported = true },
                new() { FileName = "nvngx_dlssg.dll", DllKind = "nvngx_dlssg", Feature = "FrameGeneration", MinimumVersion = "3.5.0.0", Rdna2Supported = false },
                new() { FileName = "nvngx_dlssd.dll", DllKind = "nvngx_dlssd", Feature = "RayReconstruction", MinimumVersion = "3.5.0.0", Rdna2Supported = false }
            }
        };
    }

    public async Task<IReadOnlyList<UserImportedDllRecord>> GetImportedDllsAsync(CancellationToken ct = default)
    {
        var records = (await _repo.LoadUserDllsAsync(ct)).ToList();
        foreach (var rec in records)
        {
            if (!File.Exists(rec.StoredPath))
            {
                // Check legacy folder or versioned folder fallback without touching non-existent files
                string legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ProjectLuma", "user_dlls", Path.GetFileName(rec.FileName));
                if (File.Exists(legacy))
                    rec.StoredPath = legacy;
            }
        }
        return records;
    }

    public async Task<DllValidationReport> ValidateDllAsync(
        string filePath,
        NeuralRenderingModelType targetModel = NeuralRenderingModelType.CC,
        CancellationToken ct = default)
    {
        var manifest = GetNeuralRequirementsManifest();
        string fileName = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetFileName(filePath);
        var modelReq = manifest.GetModelRequirement(targetModel);
        var companion = manifest.OptionalCompanionDlls.FirstOrDefault(c => c.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        bool isNrDll = fileName.Equals(modelReq.RequiredDll, StringComparison.OrdinalIgnoreCase);

        string expectedMinVersionStr = isNrDll
            ? modelReq.MinimumVersion
            : (companion?.MinimumVersion ?? "3.5.0.0");

        var report = new DllValidationReport
        {
            FileName = fileName,
            SourcePath = filePath ?? string.Empty,
            DllKind = isNrDll ? "nvngx_dlssnr" : (companion?.DllKind ?? string.Empty),
            TargetModel = targetModel,
            MinimumRequiredVersion = expectedMinVersionStr,
            BackendCompatibilityText = $"{manifest.BackendId} {manifest.BackendVersion}"
        };

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            report.Status = DllValidationStatus.NotImported;
            report.StatusLabel = "File not found";
            report.Message = "Selected DLL file does not exist.";
            return report;
        }

        if (!isNrDll && companion == null)
        {
            report.Status = DllValidationStatus.InvalidDll;
            report.StatusLabel = "Invalid DLL";
            report.Message = $"Unsupported filename '{fileName}'. Expected '{modelReq.RequiredDll}' (or optional companion DLLs: {string.Join(", ", manifest.OptionalCompanionDlls.Select(c => c.FileName))}).";
            return report;
        }

        // Static PE + readability + bounds validation (NEVER executes or loads DLL)
        PeValidationInfo pe = InspectPeStatic(filePath);
        report.IsReadable = pe.IsReadable;
        report.IsPeValid = pe.IsPeValid;
        report.Is64Bit = pe.Is64Bit;
        report.ArchitectureText = pe.ArchitectureText;
        report.FileSizeBytes = pe.FileSizeBytes;

        if (!pe.IsReadable)
        {
            report.Status = DllValidationStatus.FailedValidation;
            report.StatusLabel = "Failed validation";
            report.Message = $"Could not read '{fileName}': {pe.FailureDetail}";
            return report;
        }

        if (pe.IsCorrupted)
        {
            report.Status = DllValidationStatus.CorruptedFile;
            report.StatusLabel = "Corrupted file";
            report.Message = $"'{fileName}' appears truncated or corrupted: {pe.FailureDetail}";
            return report;
        }

        if (!pe.IsPeValid || !pe.IsDll)
        {
            report.Status = DllValidationStatus.InvalidDll;
            report.StatusLabel = "Invalid DLL";
            report.Message = $"'{fileName}' is not a valid Windows DLL binary: {pe.FailureDetail}";
            return report;
        }

        if (!pe.Is64Bit)
        {
            report.Status = DllValidationStatus.WrongArchitecture;
            report.StatusLabel = "Wrong architecture";
            report.Message = $"'{fileName}' targets {pe.ArchitectureText}; 64-bit x64 (PE32+ AMD64) is required.";
            return report;
        }

        Version? detectedVersion = ExtractFileVersion(filePath);
        report.DetectedVersion = detectedVersion?.ToString() ?? "Unknown";

        // Compute SHA-256 and inspect Authenticode signature safely
        report.Sha256 = await ComputeSha256Async(filePath, ct);
        (bool hasSig, string signer, string sigNote) = InspectAuthenticodeSignerDetailed(filePath);
        report.HasAuthenticSignature = hasSig;
        report.SignerSubject = signer;
        report.SignatureLimitationNote = sigNote;

        if (isNrDll)
        {
            var ccMin = ParseNormalizedVersion(manifest.GetModelRequirement(NeuralRenderingModelType.CC).MinimumVersion) ?? new Version(310, 9, 16, 0);
            var htMin = ParseNormalizedVersion(manifest.GetModelRequirement(NeuralRenderingModelType.HT).MinimumVersion) ?? new Version(310, 8, 0, 0);

            if (detectedVersion == null)
            {
                report.Status = DllValidationStatus.FailedValidation;
                report.StatusLabel = "Failed validation";
                report.Message = $"Could not read a valid file version from '{fileName}'. Required >= {expectedMinVersionStr} for {targetModel} model.";
                return report;
            }

            report.SupportsHtModel = detectedVersion >= htMin;
            report.SupportsCcModel = detectedVersion >= ccMin;

            if (detectedVersion < htMin)
            {
                report.Status = DllValidationStatus.VersionTooOld;
                report.StatusLabel = "Version too old";
                report.Message = $"Detected nvngx_dlssnr.dll v{detectedVersion}, which is older than minimum {htMin} (HT) and {ccMin} (CC).";
                return report;
            }

            if (targetModel == NeuralRenderingModelType.CC && !report.SupportsCcModel)
            {
                report.Status = DllValidationStatus.UnsupportedModel;
                report.StatusLabel = "Unsupported model";
                report.Message = $"Detected nvngx_dlssnr.dll v{detectedVersion}. CC model requires >= {ccMin}, while this file supports HT model (>= {htMin}). Switch model selection to HT or import >= {ccMin}.";
                return report;
            }

            report.BackendCompatibilityText = report.SupportsCcModel
                ? $"Compatible with DLSS-NR-on-AMD {manifest.BackendVersion} (CC & HT models)"
                : $"Compatible with DLSS-NR-on-AMD {manifest.BackendVersion} (HT model only; CC requires >= {ccMin})";
        }
        else if (companion != null)
        {
            var minComp = ParseNormalizedVersion(companion.MinimumVersion);
            if (minComp != null && detectedVersion != null && detectedVersion < minComp)
            {
                report.Status = DllValidationStatus.VersionTooOld;
                report.StatusLabel = "Version too old";
                report.Message = $"Detected {fileName} v{detectedVersion}, below minimum required {minComp}.";
                return report;
            }
            report.BackendCompatibilityText = companion.Notes;
        }

        if (!hasSig)
        {
            report.Status = DllValidationStatus.SignatureVerificationUnavailable;
            report.StatusLabel = "Compatible (signature unavailable)";
            report.Message = $"{fileName} v{report.DetectedVersion} (x64) meets model requirements, but Authenticode signature verification is unavailable ({sigNote}).";
        }
        else
        {
            report.Status = DllValidationStatus.Compatible;
            report.StatusLabel = "Compatible";
            report.Message = $"{fileName} v{report.DetectedVersion} (x64) validated cleanly. Signed by: {signer}.";
        }

        return report;
    }

    public Task<UserImportedDllRecord> ImportDllAsync(string sourceFilePath, CancellationToken ct = default)
        => ImportDllAsync(sourceFilePath, NeuralRenderingModelType.HT, ct);

    public async Task<UserImportedDllRecord> ImportDllAsync(
        string sourceFilePath,
        NeuralRenderingModelType targetModel,
        CancellationToken ct = default)
    {
        var report = await ValidateDllAsync(sourceFilePath, targetModel, ct);
        if (!report.IsAcceptableForImport)
            throw new InvalidOperationException(report.Message);

        string versionSegment = SanitizePathSegment(string.IsNullOrWhiteSpace(report.DetectedVersion) ? "unknown" : report.DetectedVersion);
        string containerDir = report.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase)
            ? NeuralRenderingVaultDirectory
            : Path.Combine(VaultDirectory, "NVIDIA", SanitizePathSegment(report.DllKind.Replace("nvngx_", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant()));

        string versionDir = Path.Combine(containerDir, versionSegment);
        Directory.CreateDirectory(versionDir);

        string destPath = Path.Combine(versionDir, report.FileName);
        string metaPath = Path.Combine(versionDir, "metadata.json");

        // Avoid duplicating identical files already stored in this version directory; clean stale different versions of the same DLL
        bool sameBinaryAlreadyStored = File.Exists(destPath) &&
            string.Equals(await ComputeSha256Async(destPath, ct), report.Sha256, StringComparison.OrdinalIgnoreCase);

        if (!sameBinaryAlreadyStored)
        {
            if (Directory.Exists(containerDir))
            {
                foreach (string existingVerDir in Directory.GetDirectories(containerDir))
                {
                    if (!Path.GetFullPath(existingVerDir).Equals(Path.GetFullPath(versionDir), StringComparison.OrdinalIgnoreCase))
                    {
                        try { Directory.Delete(existingVerDir, recursive: true); } catch { }
                    }
                }
            }

            File.Copy(sourceFilePath, destPath, overwrite: true);
            FileAttributes attrs = File.GetAttributes(destPath);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(destPath, attrs & ~FileAttributes.ReadOnly);
        }

        var compatibleModels = new List<string>();
        if (report.SupportsCcModel) compatibleModels.Add("CC");
        if (report.SupportsHtModel) compatibleModels.Add("HT");

        var record = new UserImportedDllRecord
        {
            FileName = report.FileName,
            DllKind = report.DllKind,
            OriginalSourcePath = Path.GetFullPath(sourceFilePath),
            FileVersion = report.DetectedVersion,
            Sha256 = report.Sha256,
            FileSizeBytes = new FileInfo(destPath).Length,
            IsPe64Valid = true,
            Architecture = report.ArchitectureText,
            HasAuthenticSignature = report.HasAuthenticSignature,
            SignerSubject = report.SignerSubject,
            StoredPath = destPath,
            MetadataPath = metaPath,
            SupportsCcModel = report.SupportsCcModel,
            SupportsHtModel = report.SupportsHtModel,
            CompatibleModels = compatibleModels,
            BackendCompatibility = report.BackendCompatibilityText,
            ValidationState = report.HasAuthenticSignature ? DllValidationStatus.ImportedSuccessfully : DllValidationStatus.SignatureVerificationUnavailable,
            ValidationMessage = report.Message,
            ImportedAtUtc = DateTime.UtcNow
        };

        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(record, JsonOptions), ct);
        await _repo.SaveUserDllAsync(record, ct);
        _logger.Info("UserDllVault", $"Imported {record.FileName} v{record.FileVersion} -> {destPath} (SHA-256: {record.Sha256[..12]}..., Signed={record.HasAuthenticSignature}, CC={record.SupportsCcModel}, HT={record.SupportsHtModel})");
        return record;
    }

    public async Task<DllValidationReport> VerifyImportedDllAsync(
        string fileName,
        NeuralRenderingModelType targetModel = NeuralRenderingModelType.CC,
        CancellationToken ct = default)
    {
        var existing = (await GetImportedDllsAsync(ct))
            .FirstOrDefault(d => d.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

        if (existing == null || string.IsNullOrWhiteSpace(existing.StoredPath) || !File.Exists(existing.StoredPath))
        {
            return new DllValidationReport
            {
                FileName = fileName,
                TargetModel = targetModel,
                Status = DllValidationStatus.NotImported,
                StatusLabel = "Not imported",
                Message = $"No imported copy of {fileName} is currently present in the DLL Vault."
            };
        }

        var report = await ValidateDllAsync(existing.StoredPath, targetModel, ct);
        if (!report.IsAcceptableForImport)
            return report;

        // Cross-check SHA-256 against saved metadata.json & DB record
        string expectedSha = existing.Sha256;
        if (!string.IsNullOrWhiteSpace(existing.MetadataPath) && File.Exists(existing.MetadataPath))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<UserImportedDllRecord>(await File.ReadAllTextAsync(existing.MetadataPath, ct), JsonOptions);
                if (meta != null && !string.IsNullOrWhiteSpace(meta.Sha256))
                    expectedSha = meta.Sha256;
            }
            catch
            {
                report.Status = DllValidationStatus.CorruptedFile;
                report.StatusLabel = "Corrupted metadata";
                report.Message = $"Vault metadata file '{existing.MetadataPath}' is unreadable or corrupted.";
                return report;
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha) &&
            !string.Equals(expectedSha, report.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            report.Status = DllValidationStatus.CorruptedFile;
            report.StatusLabel = "Corrupted file";
            report.Message = $"SHA-256 mismatch for vaulted {fileName}. Expected {expectedSha[..12]}..., found {report.Sha256[..12]}....";
            return report;
        }

        if (report.Status == DllValidationStatus.Compatible)
        {
            report.Status = DllValidationStatus.ImportedSuccessfully;
            report.StatusLabel = "Verified & compatible";
        }
        return report;
    }

    public async Task<IReadOnlyList<DiscoveredDllCandidate>> DiscoverCandidateDllsAsync(
        IEnumerable<string> directoriesToScan,
        string sourceCategory,
        NeuralRenderingModelType targetModel = NeuralRenderingModelType.CC,
        int maxDepth = 3,
        CancellationToken ct = default)
    {
        var candidateFiles = new List<string>();

        await Task.Run(() =>
        {
            foreach (string rawRoot in directoriesToScan
                         .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                ScanBoundedDepth(rawRoot, 0, Math.Clamp(maxDepth, 0, 3), candidateFiles, ct);
            }
        }, ct);

        var results = new List<DiscoveredDllCandidate>();
        foreach (string file in candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var validation = await ValidateDllAsync(file, targetModel, ct);
            results.Add(new DiscoveredDllCandidate
            {
                SourcePath = file,
                SourceCategory = sourceCategory,
                Validation = validation
            });
        }

        return results
            .OrderByDescending(c => c.CanImport)
            .ThenByDescending(c => c.Validation.FileName.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => ParseNormalizedVersion(c.Validation.DetectedVersion) ?? new Version(0, 0))
            .ToList();
    }

    public async Task<IReadOnlyList<UserImportedDllRecord>> ScanGameLibrariesForUserDllsAsync(
        IEnumerable<string> directoriesToScan,
        CancellationToken ct = default)
    {
        var imported = new List<UserImportedDllRecord>();
        var candidateFiles = new List<string>();

        await Task.Run(() =>
        {
            foreach (string rawRoot in directoriesToScan
                         .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                ScanBoundedDepth(rawRoot, 0, 3, candidateFiles, ct);
            }
        }, ct);

        // Prioritize nvngx_dlssnr.dll first, sorted by newest version if multiple found
        foreach (string file in candidateFiles
                     .OrderByDescending(f => Path.GetFileName(f).Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(f => ExtractFileVersion(f) ?? new Version(0, 0)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rec = await ImportDllAsync(file, NeuralRenderingModelType.HT, ct);
                imported.Add(rec);
            }
            catch
            {
                // Skip invalid or outdated DLL candidates cleanly while keeping valid ones
            }
        }

        return imported;
    }

    private static void ScanBoundedDepth(string dir, int depth, int maxDepth, List<string> results, CancellationToken ct)
    {
        if (depth > maxDepth || ct.IsCancellationRequested) return;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "nvngx_dlss*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("nvngx_dlssd.dll", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(file);
                }
            }

            if (depth < maxDepth)
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    ScanBoundedDepth(sub, depth + 1, maxDepth, results, ct);
                }
            }
        }
        catch
        {
            // Ignore inaccessible directories
        }
    }

    public async Task DeleteDllAsync(string fileName, CancellationToken ct = default)
    {
        string safeName = Path.GetFileName(fileName);
        var existing = (await _repo.LoadUserDllsAsync(ct))
            .FirstOrDefault(d => d.FileName.Equals(safeName, StringComparison.OrdinalIgnoreCase));

        if (existing != null && !string.IsNullOrWhiteSpace(existing.StoredPath) && File.Exists(existing.StoredPath))
        {
            string? parentDir = Path.GetDirectoryName(existing.StoredPath);
            if (!string.IsNullOrWhiteSpace(parentDir) &&
                parentDir.StartsWith(Path.GetFullPath(VaultDirectory), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(parentDir))
            {
                try { Directory.Delete(parentDir, recursive: true); } catch { }
            }
            else
            {
                try { File.Delete(existing.StoredPath); } catch { }
            }
        }

        if (safeName.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase) && Directory.Exists(NeuralRenderingVaultDirectory))
        {
            foreach (string dir in Directory.GetDirectories(NeuralRenderingVaultDirectory))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        string legacyTarget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectLuma", "user_dlls", safeName);
        if (File.Exists(legacyTarget))
        {
            try { File.Delete(legacyTarget); } catch { }
        }

        await _repo.RemoveUserDllAsync(safeName, ct);
        _logger.Info("UserDllVault", $"Removed user DLL {safeName} from local vault.");
    }

    public static bool IsValidPe64Dll(string path)
    {
        var pe = InspectPeStatic(path);
        return pe.IsReadable && !pe.IsCorrupted && pe.IsPeValid && pe.IsDll && pe.Is64Bit;
    }

    private static PeValidationInfo InspectPeStatic(string path)
    {
        var info = new PeValidationInfo();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            info.IsReadable = true;
            info.FileSizeBytes = fs.Length;

            if (fs.Length < 128)
            {
                info.IsCorrupted = true;
                info.FailureDetail = $"File size ({fs.Length} bytes) is too small to contain valid PE headers.";
                return info;
            }

            using var br = new BinaryReader(fs);
            ushort dosMagic = br.ReadUInt16();
            if (dosMagic != 0x5A4D)
            {
                info.FailureDetail = "Missing DOS 'MZ' header signature.";
                return info;
            }

            fs.Position = 0x3C;
            uint peOffset = br.ReadUInt32();
            if (peOffset < 64 || peOffset + 26L > fs.Length)
            {
                info.IsCorrupted = true;
                info.FailureDetail = "Invalid or truncated PE header offset.";
                return info;
            }

            fs.Position = peOffset;
            uint peSig = br.ReadUInt32();
            if (peSig != 0x00004550)
            {
                info.FailureDetail = "Missing 'PE\\0\\0' signature.";
                return info;
            }

            ushort machine = br.ReadUInt16();
            ushort numberOfSections = br.ReadUInt16();
            fs.Position += 12; // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
            ushort sizeOfOptionalHeader = br.ReadUInt16();
            ushort characteristics = br.ReadUInt16();

            info.ArchitectureText = machine switch
            {
                0x8664 => "x64 (AMD64)",
                0x014C => "x86 (32-bit)",
                0xAA64 => "ARM64",
                _ => $"Unknown (0x{machine:X4})"
            };

            long optStart = fs.Position;
            if (sizeOfOptionalHeader < 2 || optStart + sizeOfOptionalHeader > fs.Length)
            {
                info.IsCorrupted = true;
                info.FailureDetail = "Optional PE header extends past end of file.";
                return info;
            }

            ushort optMagic = br.ReadUInt16();
            info.IsPeValid = true;
            info.IsDll = (characteristics & 0x2000) != 0;
            if (!info.IsDll)
            {
                info.FailureDetail = "PE image does not have the IMAGE_FILE_DLL characteristic flag.";
                return info;
            }

            info.Is64Bit = machine == 0x8664 && optMagic == 0x020B;

            long sectionTableStart = optStart + sizeOfOptionalHeader;
            long sectionTableEnd = sectionTableStart + (numberOfSections * 40L);
            if (numberOfSections > 96 || sectionTableEnd > fs.Length)
            {
                info.IsCorrupted = true;
                info.FailureDetail = "PE section table is truncated or malformed.";
                return info;
            }

            for (int i = 0; i < numberOfSections; i++)
            {
                fs.Position = sectionTableStart + (i * 40L) + 16;
                uint sizeOfRawData = br.ReadUInt32();
                uint ptrToRawData = br.ReadUInt32();
                if (ptrToRawData > 0 && (long)ptrToRawData + sizeOfRawData > fs.Length)
                {
                    info.IsCorrupted = true;
                    info.FailureDetail = $"PE section #{i + 1} extends beyond file length ({fs.Length} bytes).";
                    return info;
                }
            }

            return info;
        }
        catch (Exception ex)
        {
            info.IsReadable = false;
            info.FailureDetail = ex.Message;
            return info;
        }
    }

    public Version? ExtractFileVersion(string filePath)
    {
        if (_versionOverride != null)
        {
            var overridden = _versionOverride(filePath);
            if (overridden != null) return overridden;
        }

        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(filePath);
            if (fvi.FileMajorPart > 0 || fvi.FileMinorPart > 0 || fvi.FileBuildPart > 0 || fvi.FilePrivatePart > 0)
                return new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart, fvi.FilePrivatePart);

            string raw = !string.IsNullOrWhiteSpace(fvi.FileVersion) ? fvi.FileVersion : (fvi.ProductVersion ?? string.Empty);
            return ParseNormalizedVersion(raw);
        }
        catch
        {
            return null;
        }
    }

    public static Version? ParseNormalizedVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Regex.Match(raw.Trim(), @"(\d+)[.,\s]+(\d+)[.,\s]+(\d+)(?:[.,\s]+(\d+))?");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out int maj) &&
            int.TryParse(m.Groups[2].Value, out int min) &&
            int.TryParse(m.Groups[3].Value, out int bld))
        {
            int rev = m.Groups[4].Success && int.TryParse(m.Groups[4].Value, out int r) ? r : 0;
            return new Version(maj, min, bld, rev);
        }

        string clean = raw.Split(' ', '-', '+')[0].Replace(',', '.').Trim();
        return Version.TryParse(clean, out var parsed) ? parsed : null;
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    public static (bool HasSignature, string Subject) InspectAuthenticodeSigner(string filePath)
    {
        var res = InspectAuthenticodeSignerDetailed(filePath);
        return (res.HasSignature, res.Subject);
    }

    public static (bool HasSignature, string Subject, string LimitationNote) InspectAuthenticodeSignerDetailed(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert2 = new X509Certificate2(cert);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            bool chainOk = chain.Build(cert2);
            if (!chainOk)
            {
                return (true, cert2.Subject, "Authenticode certificate present, though offline/local trust chain could not be fully verified.");
            }
            return (true, cert2.Subject, "Authenticode signer certificate verified.");
#pragma warning restore SYSLIB0057
        }
        catch
        {
            return (false, "Unsigned / Signature verification unavailable", "Embedded Authenticode signature not present or could not be verified offline.");
        }
    }

    private static string SanitizePathSegment(string value)
        => string.Concat((value ?? "unknown").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string ResolveRequirementsManifestPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "data", "backend_manifests", "neural_rendering_requirements.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "backend_manifests", "neural_rendering_requirements.json")),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "backend_manifests", "neural_rendering_requirements.json")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
