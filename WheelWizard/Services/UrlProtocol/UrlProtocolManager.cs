using WheelWizard.Views.Popups.Generic;
using WheelWizard.Views.Popups.ModManagement;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace WheelWizard.Services.UrlProtocol;

public static class UrlProtocolManager
{
    private const string ProtocolName = "wheelwizardvr";

#if WINDOWS
    private static void RegisterCustomScheme(string schemeName)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var currentExecutablePath = Environment.ProcessPath;
        var protocolKey = $@"SOFTWARE\Classes\{schemeName}";

        using var key = Registry.CurrentUser.CreateSubKey(protocolKey);

        key.SetValue("", $"URL:{schemeName} Protocol");
        key.SetValue("URL Protocol", "");

        using var shellKey = key.CreateSubKey(@"shell\open\command");
        shellKey.SetValue("", $"\"{currentExecutablePath}\" \"%1\"");
    }

    private static void SetWhWzSchemeInternally()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var currentExecutablePath = Environment.ProcessPath;
        const string protocolKey = $@"SOFTWARE\Classes\{ProtocolName}";

        // Check if the scheme is registered
        using var key = Registry.CurrentUser.OpenSubKey(protocolKey);
        if (key == null)
        {
            RegisterCustomScheme(ProtocolName);
            return;
        }

        using var shellKey = key.OpenSubKey(@"shell\open\command");

        if (shellKey?.GetValue("") is not string registeredExecutablePath)
            return;

        // Extract the path from the registered string (which might have quotes and "%1")
        registeredExecutablePath = registeredExecutablePath.Split('\"')[1];

        // If the registered executable is different from the current one, repair it
        if (!registeredExecutablePath.Equals(currentExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            // Fix the scheme by re-registering with the current executable
            RegisterCustomScheme(ProtocolName);
        }
    }
#endif

    public static void SetWhWzScheme()
    {
#if WINDOWS
        SetWhWzSchemeInternally();
#endif
    }

    public static async Task ShowPopupForLaunchUrlAsync(string url)
    {
        // Remove the protocol prefix
        var content = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..].Trim().TrimEnd('/');
        var parts = content.Split(',');
        try
        {
            if (!int.TryParse(parts[0], out var modId))
                throw new FormatException($"Invalid ModID: {parts[0]}");

            var downloadUrl = parts.Length > 1 ? parts[1] : null;
            var modPopup = new ModIndependentWindow();
            await modPopup.LoadModAsync(modId, downloadUrl);
            await modPopup.ShowDialog();
        }
        catch (Exception ex)
        {
            new MessageBoxWindow()
                .SetMessageType(MessageBoxWindow.MessageType.Error)
                .SetTitleText("Couldn't load URL")
                .SetInfoText($"Error handling URL: {ex.Message}")
                .Show();
        }
    }
}
