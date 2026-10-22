using WheelWizard.ApplicationData;
using WheelWizard.CustomDistributions;
using WheelWizard.Models.Enums;
using WheelWizard.Recomp;

namespace WheelWizard.Launching;

public sealed class RrBetaLauncher(
    IRetroRewindLaunchService launch,
    ICustomDistributionSingletonService distributions,
    IDistributionOperationPresentation presentation,
    IApplicationDataLocation applicationData
) : ILauncher
{
    public string GameTitle => "Retro Rewind Beta";

    public Task<OperationResult> Launch() => launch.LaunchAsync(true);

    public Task<OperationResult> Install() =>
        RecompOperationCoordinator.RunAsync(
            applicationData.DirectoryPath,
            () => presentation.RunAsync(distributions.RetroRewindBeta, DistributionAction.Install)
        );

    public Task<OperationResult> Update() =>
        RecompOperationCoordinator.RunAsync(
            applicationData.DirectoryPath,
            () => presentation.RunAsync(distributions.RetroRewindBeta, DistributionAction.Update)
        );

    public async Task<WheelWizardStatus> GetCurrentStatus()
    {
        var result = await distributions.RetroRewindBeta.GetCurrentStatusAsync();
        return result.IsSuccess ? result.Value : WheelWizardStatus.NotInstalled;
    }
}
