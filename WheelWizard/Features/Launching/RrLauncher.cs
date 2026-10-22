using WheelWizard.ApplicationData;
using WheelWizard.CustomDistributions;
using WheelWizard.Models.Enums;
using WheelWizard.Recomp;

namespace WheelWizard.Launching;

public sealed class RrLauncher(
    IRetroRewindLaunchService launch,
    ICustomDistributionSingletonService distributions,
    IDistributionOperationPresentation presentation,
    IApplicationDataLocation applicationData
) : ILauncher
{
    public string GameTitle => "Retro Rewind";

    public Task<OperationResult> Launch() => launch.LaunchAsync(false);

    public Task<OperationResult> Install() =>
        RecompOperationCoordinator.RunAsync(
            applicationData.DirectoryPath,
            () => presentation.RunAsync(distributions.RetroRewind, DistributionAction.Install)
        );

    public Task<OperationResult> Update() =>
        RecompOperationCoordinator.RunAsync(
            applicationData.DirectoryPath,
            () => presentation.RunAsync(distributions.RetroRewind, DistributionAction.Update)
        );

    public async Task<WheelWizardStatus> GetCurrentStatus()
    {
        var result = await distributions.RetroRewind.GetCurrentStatusAsync();
        return result.IsSuccess ? result.Value : WheelWizardStatus.NotInstalled;
    }
}
