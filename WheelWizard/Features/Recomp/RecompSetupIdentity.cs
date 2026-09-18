using System.Text.Json;

namespace WheelWizard.Recomp;

public static class RecompSetupIdentity
{
    public static bool IsMatchingVr(string json, string expectedVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.GetProperty("productId").GetString() == RecompBackend.VrProductId
                && root.GetProperty("openxrD3D12").GetBoolean()
                && RecompVersion.TryParse(root.GetProperty("version").GetString(), out var actual)
                && RecompVersion.TryParse(expectedVersion, out var expected)
                && actual.ComparePrecedenceTo(expected) == 0;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Whether an <c>--info-json</c> line advertises <c>--build-quest</c>, which older setups lack.</summary>
    public static bool SupportsQuestBuild(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("questBuild", out var questBuild) && questBuild.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
