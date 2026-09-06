// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Text;

namespace Foundry.Core.Services.WinPe;

public sealed class WinPeEmbeddedAssetService : IWinPeEmbeddedAssetService
{
    internal const string UsbDiskOperationsResourceName = "Foundry.Core.WinPe.UsbDiskOperations";
    internal const string UsbProvisioningScriptResourceName = "Foundry.Core.WinPe.ProvisionUsbDisk";
    private const string BootstrapResourceName = "Foundry.Core.WinPe.FoundryBootstrap";
    private const string TimeZoneMapResourceName = "Foundry.Core.Configuration.IanaWindowsTimeZones";

    public string GetBootstrapScriptContent()
    {
        return ReadEmbeddedText(BootstrapResourceName);
    }

    public string GetUsbProvisioningScriptTemplateContent()
    {
        return ReadEmbeddedText(UsbProvisioningScriptResourceName);
    }

    public string GetIanaWindowsTimeZoneMapJson()
    {
        return ReadEmbeddedText(TimeZoneMapResourceName);
    }

    public string GetSevenZipSourceDirectoryPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "Assets", "7z");
    }

    internal static string ReadEmbeddedText(string resourceName)
    {
        Assembly assembly = typeof(WinPeEmbeddedAssetService).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded WinPE asset resource '{resourceName}' was not found.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
