using WheelWizard.Recomp.Domain;

namespace WheelWizard.Recomp;

/// <summary>
/// Builds the command lines described by the WheelWizard-Recomp integration contract.
/// The recomp owns the actual behaviour; WheelWizard only decides which of its own paths to hand over.
/// Every method returns the argument vector as separate values: the process runner hands them to
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so no quoting happens here.
/// </summary>
public static class RecompSetupCommandBuilder
{
    /// <summary>
    /// The name of the setup asset on every recomp GitHub release, and of the launcher copy inside the install dir.
    /// </summary>
    public const string SetupFileName = "WiiCompiled-Setup.exe";

    /// <summary>
    /// Builds the arguments for a silent install, which doubles as the in-place upgrade command.
    /// </summary>
    public static IReadOnlyList<string> BuildSilentInstallArguments(RecompInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GameFilePath))
            throw new ArgumentException("A game image path is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.InstallFolderPath))
            throw new ArgumentException("An install directory is required.", nameof(request));

        var arguments = new List<string> { "--silent", "--game", request.GameFilePath, "--install-dir", request.InstallFolderPath };

        // Only a silent install may request the portable layout, and only when WheelWizard actually
        // handed over its own portable location. An installation that predates portable mode keeps
        // its machine-local layout for the rest of its life.
        if (request.Portable)
            arguments.Add("--portable");

        arguments.Add("--progress-json");

        if (!string.IsNullOrWhiteSpace(request.RetroRewindFolderPath))
        {
            arguments.Add("--retro-dir");
            arguments.Add(request.RetroRewindFolderPath);
            arguments.Add(RetroWfcPayloadArgument(request.RetroWfcPayloadMode));
        }

        return arguments;
    }

    /// <summary>
    /// Builds a repair that performs only the work the installed products actually need, using the
    /// toolkit the installation already owns instead of an embedded payload. Every repair receives the
    /// Retro Rewind source and exactly one Retro-WFC payload option, as the contract requires.
    /// </summary>
    public static IReadOnlyList<string> BuildRepairProductsArguments(
        string installFolderPath,
        string retroRewindFolderPath,
        RecompRetroWfcPayloadMode retroWfcPayloadMode = RecompRetroWfcPayloadMode.Download
    )
    {
        if (string.IsNullOrWhiteSpace(installFolderPath))
            throw new ArgumentException("An install directory is required.", nameof(installFolderPath));
        if (string.IsNullOrWhiteSpace(retroRewindFolderPath))
            throw new ArgumentException("A Retro Rewind source directory is required.", nameof(retroRewindFolderPath));

        return
        [
            "--repair-products",
            "--install-dir",
            installFolderPath,
            "--retro-dir",
            retroRewindFolderPath,
            RetroWfcPayloadArgument(retroWfcPayloadMode),
            "--progress-json",
        ];
    }

    /// <summary>
    /// Builds the arguments used to start the game. WheelWizard is the Retro Rewind frontend, so it starts
    /// Retro Rewind whenever that product is installed and only falls back to the unmodded game otherwise.
    /// </summary>
    public static IReadOnlyList<string> BuildLaunchArguments(bool retroRewind) => [retroRewind ? "--launch-retro" : "--launch-base"];

    /// <summary>
    /// Builds the arguments that report, without building anything, whether the installed executables are
    /// still the output of the installed toolkit and the installed <c>Code.pul</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildCheckProductsArguments(string installFolderPath, string? retroRewindFolderPath = null)
    {
        if (string.IsNullOrWhiteSpace(installFolderPath))
            throw new ArgumentException("An install directory is required.", nameof(installFolderPath));

        var arguments = new List<string> { "--check-products", "--install-dir", installFolderPath };
        if (!string.IsNullOrWhiteSpace(retroRewindFolderPath))
        {
            arguments.Add("--retro-dir");
            arguments.Add(retroRewindFolderPath);
        }

        arguments.Add("--progress-json");
        return arguments;
    }

    /// <summary>
    /// Builds the arguments that compile the installation's translated game for the Meta Quest app whose APK
    /// the user chose, and package it at <paramref name="outputFilePath"/>. Game files are opt-in because
    /// they make the package several gigabytes larger.
    /// </summary>
    public static IReadOnlyList<string> BuildQuestArguments(
        string installFolderPath,
        string questApkPath,
        string outputFilePath,
        bool includeGameFiles,
        RecompGame game = RecompGame.Base,
        string? modContentFolderPath = null
    )
    {
        if (string.IsNullOrWhiteSpace(installFolderPath))
            throw new ArgumentException("An install directory is required.", nameof(installFolderPath));
        if (string.IsNullOrWhiteSpace(questApkPath))
            throw new ArgumentException("The Quest app's APK is required.", nameof(questApkPath));
        if (string.IsNullOrWhiteSpace(outputFilePath))
            throw new ArgumentException("An output file is required.", nameof(outputFilePath));
        if (!string.IsNullOrWhiteSpace(modContentFolderPath) && game != RecompGame.RetroRewind)
            throw new ArgumentException("Only Retro Rewind carries mod content.", nameof(modContentFolderPath));

        var arguments = new List<string>
        {
            "--build-quest",
            "--install-dir",
            installFolderPath,
            "--quest-apk",
            questApkPath,
            "--output",
            outputFilePath,
            "--quest-product",
            game == RecompGame.RetroRewind ? "retro_rewind" : "base",
        };
        if (includeGameFiles)
            arguments.Add("--include-game-files");

        // The headset has no way of its own to fetch the Retro Rewind pack, so the game file can
        // carry the copy this installation already keeps.
        if (!string.IsNullOrWhiteSpace(modContentFolderPath))
        {
            arguments.Add("--retro-dir");
            arguments.Add(modContentFolderPath);
            arguments.Add("--include-mod-content");
        }

        arguments.Add("--progress-json");
        return arguments;
    }

    /// <summary>
    /// Builds the arguments that make the setup executable print its own semantic version.
    /// </summary>
    public static IReadOnlyList<string> BuildVersionArguments() => ["--version"];

    // The contract requires exactly one payload option whenever --retro-dir is passed. WheelWizard
    // downloads unless the payload service is unreachable and the user chose an offline-only build.
    private static string RetroWfcPayloadArgument(RecompRetroWfcPayloadMode mode) =>
        mode == RecompRetroWfcPayloadMode.Skip ? "--skip-retro-wfc-payload" : "--download-retro-wfc-payload";
}
