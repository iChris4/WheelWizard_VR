using System.Globalization;

namespace WheelWizard.Recomp;

public static class RecompVideoConfig
{
    private const int WiiInternalWidth = 640;
    private const int WiiInternalHeight = 528;

    public static IReadOnlyList<double> ResolutionMultipliers { get; } = [1.0, 1.5, 2.0, 3.0];

    /// <summary>
    /// The headset render scales WheelWizard offers, as a list rather than a free number: every change
    /// rewrites the recomp's Config.toml, so a spinner writes the file once per step.
    /// </summary>
    public static IReadOnlyList<double> RenderScales { get; } = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>
    /// The graphics APIs WheelWizard offers, in the order they are shown. The backend enumerates more
    /// (auto, d3d11, opengl, ...), but only these two actually run the game reliably, so nothing else
    /// is ever offered. A value outside this list stays untouched until the user picks one of these.
    /// </summary>
    public static IReadOnlyList<string> OfferedGraphicsApis { get; } = ["d3d12", "vulkan"];

    /// <summary>The label for a graphics API value, for example <c>DirectX 12</c> for <c>d3d12</c>.</summary>
    public static string DescribeGraphicsApi(string api) =>
        api switch
        {
            "d3d12" => "DirectX 12",
            "vulkan" => "Vulkan",
            _ => api,
        };

    /// <summary>The label for a render scale, for example <c>1.00x</c>, with the default marked.</summary>
    public static string DescribeRenderScale(double scale) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.00}x", scale) + (Math.Abs(scale - 1.0) < 0.001 ? " (default)" : string.Empty);

    /// <summary>
    /// The offered scales, with a value set outside WheelWizard (the in-game panel takes any number)
    /// folded in, so the list never reports a scale the game is not actually using.
    /// </summary>
    public static IReadOnlyList<double> RenderScalesIncluding(double scale)
    {
        if (!double.IsFinite(scale) || scale < RenderScales[0] || scale > RenderScales[^1])
            return RenderScales;
        if (RenderScales.Any(offered => Math.Abs(offered - scale) < 0.001))
            return RenderScales;

        var scales = RenderScales.ToList();
        scales.Add(Math.Round(scale, 2));
        scales.Sort();
        return scales;
    }

    /// <summary>The index in <paramref name="scales"/> nearest <paramref name="scale"/>.</summary>
    public static int FindClosestRenderScaleIndex(IReadOnlyList<double> scales, double scale)
    {
        if (!double.IsFinite(scale))
            return scales.ToList().IndexOf(1.0);

        var closest = 0;
        for (var index = 1; index < scales.Count; index++)
        {
            if (Math.Abs(scales[index] - scale) < Math.Abs(scales[closest] - scale))
                closest = index;
        }

        return closest;
    }

    /// <summary>The label for a multiplier, for example <c>2.0x (1280x1056)</c>.</summary>
    public static string DescribeResolution(double multiplier)
    {
        var width = (int)Math.Round(WiiInternalWidth * multiplier);
        var height = (int)Math.Round(WiiInternalHeight * multiplier);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}x ({1}x{2})", multiplier, width, height);
    }
}
