// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Foundry.Core.Models.Configuration;

namespace Foundry.Core.Services.Configuration;

/// <summary>
/// Validates immutable source snapshots without rewriting XML or exposing its contents in errors.
/// </summary>
public static class UnattendFileService
{
    /// <summary>
    /// Limits both imported plaintext and decrypted runtime answer files to 4 MiB.
    /// </summary>
    public const int MaximumFileSizeBytes = 4 * 1024 * 1024;

    private static readonly XNamespace UnattendNamespace = "urn:schemas-microsoft-com:unattend";

    /// <summary>
    /// Records a source reference and digest after validating one bounded snapshot.
    /// </summary>
    public static UnattendFileSettings Import(string sourcePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            throw new InvalidOperationException("The answer-file source path is invalid.");
        }

        byte[] content = ReadSource(fullPath);
        try
        {
            Inspect(content);
            return new UnattendFileSettings
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = CreateDisplayName(fullPath),
                SourcePath = fullPath,
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(content))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    /// <summary>
    /// Returns the exact validated bytes; the caller owns and must clear this sensitive buffer.
    /// Changed sources require an explicit refresh instead of silently accepting new content.
    /// </summary>
    public static byte[] ReadValidated(UnattendFileSettings file)
    {
        if (file is null || !IsValidMetadata(file))
        {
            throw new InvalidDataException("The answer-file source metadata is invalid.");
        }

        byte[] content = ReadSource(file.SourcePath);
        try
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!string.Equals(hash, file.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The answer-file source changed. Refresh the imported file before building media.");
            }

            Inspect(content);
            return content;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(content);
            throw;
        }
    }

    /// <summary>
    /// Checks supported passes and applicable component settings without certifying Windows SIM compatibility.
    /// Auxiliary architectures remain unchanged; only applicable components contribute conflict signals.
    /// </summary>
    public static UnattendInspection Inspect(byte[] content, string? targetArchitecture = null)
    {
        XDocument document = Parse(content);
        XElement root = document.Root!;
        if (root.Name != UnattendNamespace + "unattend")
        {
            throw new InvalidDataException("The answer file must use the Windows unattend root and namespace.");
        }

        if (root.Elements(UnattendNamespace + "servicing").Any(HasContent))
        {
            throw new InvalidDataException("The answer file contains unsupported offline servicing instructions. Only specialize and oobeSystem are supported.");
        }

        string? target = targetArchitecture is null ? null : NormalizeArchitecture(targetArchitecture);
        var architectures = new HashSet<string>(StringComparer.Ordinal);
        bool hasApplicableSettings = false;
        bool hasCommands = false;
        bool conflictsWithAutopilot = false;
        foreach (XElement settings in root.Elements(UnattendNamespace + "settings"))
        {
            string? pass = (string?)settings.Attribute("pass");
            if (pass is not ("specialize" or "oobeSystem"))
            {
                if (HasContent(settings))
                {
                    string passLabel = pass is "windowsPE" or "offlineServicing" or "generalize" or "auditSystem" or "auditUser" ? pass : "unknown";
                    throw new InvalidDataException($"The answer file contains unsupported settings in the {passLabel} pass. Only specialize and oobeSystem are supported.");
                }

                continue;
            }

            foreach (XElement component in settings.Elements(UnattendNamespace + "component"))
            {
                if (string.IsNullOrWhiteSpace((string?)component.Attribute("name")) || !component.Elements().Any(element => element.Name.Namespace == UnattendNamespace))
                {
                    continue;
                }

                string architecture = NormalizeArchitecture((string?)component.Attribute("processorArchitecture") ?? "*");
                architectures.Add(architecture);
                string name = (string)component.Attribute("name")!;
                if (name == "Microsoft-Windows-Deployment" &&
                    Children(component, "Reseal", "Mode").Any(mode => string.Equals(mode.Value.Trim(), "Audit", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("Audit-mode resealing is not supported for normal Windows deployment.");
                }

                if (target is not null && architecture != target && architecture is not ("*" or "neutral"))
                {
                    continue;
                }

                hasApplicableSettings = true;

                hasCommands |= HasCommands(component, name, pass);
                conflictsWithAutopilot |= ConflictsWithAutopilot(component, name, pass);
            }
        }

        if (!hasApplicableSettings)
        {
            throw new InvalidDataException("The answer file has no supported component settings applicable to the selected architecture.");
        }

        return new UnattendInspection
        {
            Architectures = architectures.Order(StringComparer.Ordinal).ToArray(),
            HasCommands = hasCommands,
            ConflictsWithAutopilot = conflictsWithAutopilot
        };
    }

    /// <summary>
    /// Validates enabled catalog metadata and protection; source snapshots are validated when read for packaging.
    /// Disabled catalogs are retained without requiring their sources to remain available.
    /// </summary>
    public static void ValidateSettings(UnattendSettings settings, bool isProtected)
    {
        if (settings is null)
        {
            throw new InvalidOperationException("The answer-file catalog is invalid.");
        }

        if (!settings.IsEnabled)
        {
            return;
        }

        if (!isProtected)
        {
            throw new InvalidOperationException("Custom answer files require deployment media password protection.");
        }

        if (settings.Files is null || settings.Files.Count == 0)
        {
            throw new InvalidOperationException("Import at least one answer file before enabling custom answer files.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UnattendFileSettings file in settings.Files)
        {
            if (file is null || !IsValidMetadata(file) || !ids.Add(file.Id) || !hashes.Add(file.ContentHash))
            {
                throw new InvalidOperationException("The answer-file catalog contains invalid or duplicate entries.");
            }
        }

        if (settings.DefaultFileId is not null && !ids.Contains(settings.DefaultFileId))
        {
            throw new InvalidOperationException("The default answer file is missing from the catalog. Select an available default.");
        }
    }

    /// <summary>
    /// Derives an application-owned filename from a generated identifier, rejecting arbitrary paths.
    /// </summary>
    public static string GetAssetFileName(string id)
    {
        if (!IsHex(id, 32))
        {
            throw new InvalidDataException("The answer-file identifier is invalid.");
        }

        return id.ToLowerInvariant() + ".xml.encrypted";
    }

    private static byte[] ReadSource(string sourcePath)
    {
        try
        {
            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileSizeBytes)
            {
                throw new InvalidDataException("The answer file exceeds the 4 MiB limit.");
            }

            byte[] buffer = new byte[MaximumFileSizeBytes + 1];
            try
            {
                int length = 0;
                int read;
                while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
                {
                    length += read;
                }

                if (length > MaximumFileSizeBytes)
                {
                    throw new InvalidDataException("The answer file exceeds the 4 MiB limit.");
                }

                return buffer.AsSpan(0, length).ToArray();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("The answer-file source is unavailable. Restore or refresh the imported file before building media.");
        }
    }

    private static XDocument Parse(byte[] content)
    {
        if (content is null || content.Length == 0 || content.Length > MaximumFileSizeBytes)
        {
            throw new InvalidDataException("The answer file must contain XML and be no larger than 4 MiB.");
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumFileSizeBytes,
                MaxCharactersFromEntities = MaximumFileSizeBytes
            });
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new InvalidDataException("The answer file contains invalid or prohibited XML. Validate it with Windows System Image Manager.");
        }
    }

    private static string NormalizeArchitecture(string architecture)
    {
        return architecture.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" => "amd64",
            "aarch64" or "arm64" => "arm64",
            "x86" => "x86",
            "arm" => "arm",
            "neutral" => "neutral",
            "*" => "*",
            _ => throw new InvalidDataException("The answer file or selected image declares an unsupported architecture.")
        };
    }

    private static bool HasCommands(XElement component, string name, string pass)
    {
        return (name == "Microsoft-Windows-Deployment" && pass == "specialize" &&
                (Children(component, "RunSynchronous", "RunSynchronousCommand").Any() || Children(component, "RunAsynchronous", "RunAsynchronousCommand").Any())) ||
            (name == "Microsoft-Windows-Shell-Setup" && pass == "oobeSystem" &&
                (Children(component, "FirstLogonCommands", "SynchronousCommand").Any() || Children(component, "LogonCommands", "AsynchronousCommand").Any()));
    }

    private static bool ConflictsWithAutopilot(XElement component, string name, string pass)
    {
        if (name == "Microsoft-Windows-UnattendedJoin" && pass == "specialize")
        {
            return Children(component, "Identification", "JoinDomain").Any(element => !string.IsNullOrWhiteSpace(element.Value)) ||
                Children(component, "Identification", "Provisioning", "AccountData").Any(element => !string.IsNullOrWhiteSpace(element.Value));
        }

        if (name != "Microsoft-Windows-Shell-Setup")
        {
            return false;
        }

        if (Children(component, "AutoLogon", "Enabled").Any(IsTrue))
        {
            return true;
        }

        return pass == "oobeSystem" &&
            (Children(component, "UserAccounts", "LocalAccounts", "LocalAccount").Any() ||
             Children(component, "OOBE", "SkipMachineOOBE").Any(IsTrue) ||
             Children(component, "OOBE", "SkipUserOOBE").Any(IsTrue) ||
             Children(component, "OOBE", "HideOnlineAccountScreens").Any(IsTrue) ||
             Children(component, "OOBE", "HideLocalAccountScreen").Any(IsTrue));
    }

    private static IEnumerable<XElement> Children(XElement component, params string[] names)
    {
        IEnumerable<XElement> elements = [component];
        foreach (string name in names)
        {
            elements = elements.Elements(UnattendNamespace + name);
        }

        return elements;
    }

    private static bool IsTrue(XElement element)
    {
        string value = element.Value.Trim();
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidMetadata(UnattendFileSettings file)
    {
        return IsHex(file.Id, 32) && IsHex(file.ContentHash, 64) &&
            !string.IsNullOrWhiteSpace(file.DisplayName) && file.DisplayName.Length <= 200 &&
            !file.DisplayName.Any(char.IsControl) && !string.IsNullOrWhiteSpace(file.SourcePath);
    }

    private static string CreateDisplayName(string fullPath)
    {
        string name = string.Concat(Path.GetFileName(fullPath).Select(character => char.IsControl(character) ? '_' : character));
        int length = Math.Min(name.Length, 200);
        if (length < name.Length && char.IsHighSurrogate(name[length - 1]))
        {
            length--;
        }

        return name[..length];
    }

    private static bool HasContent(XElement element)
    {
        return element.Elements().Any() || element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value));
    }

    private static bool IsHex(string? value, int length)
    {
        return value is not null && value.Length == length && value.All(Uri.IsHexDigit);
    }
}
