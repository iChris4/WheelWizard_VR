namespace WheelWizard.GitHub.Domain;

public class GithubAsset
{
    public required string BrowserDownloadUrl { get; set; }

    public required string Name { get; set; }

    /// <summary>The asset's size in bytes, as GitHub reports it; absent from listings that predate the field.</summary>
    public long? Size { get; set; }
}
