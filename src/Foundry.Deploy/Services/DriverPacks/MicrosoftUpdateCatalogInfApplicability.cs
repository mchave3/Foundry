// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.IO;
using System.Text;
using Foundry.Deploy.Models;

namespace Foundry.Deploy.Services.DriverPacks;

/// <summary>Proves model applicability for the selected Windows client target before candidate publication.</summary>
internal static class MicrosoftUpdateCatalogInfApplicability
{
    private const int MaximumContentCharacters = 4 * 1024 * 1024;
    private const int MaximumFieldCharacters = 4095;
    private const string FirmwareClassGuid = "{f2e7dd72-6468-4e36-b6f1-6488f42c1b52}";

    /// <summary>
    /// Selects the closest applicable Models section per Manufacturer entry, then matches exact hardware IDs.
    /// This bounded proof supports x64/ARM64 Windows client targets; unknown syntax or suite requirements fail closed.
    /// It does not replace Windows driver signature verification or installation validation.
    /// </summary>
    internal static bool IsApplicable(string infContent, OperatingSystemCatalogItem target,
        IReadOnlyCollection<string> hardwareIds, bool requireFirmware = false)
    {
        string architecture = target.Architecture.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" => "ntamd64",
            "arm64" or "aarch64" => "ntarm64",
            _ => string.Empty
        };
        if (architecture.Length == 0 || target.BuildMajor <= 0 || hardwareIds.Count == 0 ||
            string.IsNullOrWhiteSpace(infContent) || infContent.Length > MaximumContentCharacters)
        {
            return false;
        }

        try
        {
            Dictionary<string, List<string>> sections = ReadSections(infContent);
            Dictionary<string, string> strings = ReadStrings(sections);
            string signature = ReadDirective(sections, strings, "Signature");
            string className = ReadDirective(sections, strings, "Class");
            string classGuid = ReadDirective(sections, strings, "ClassGuid");
            bool firmwareClass = className.Equals("Firmware", StringComparison.OrdinalIgnoreCase);
            bool firmwareGuid = classGuid.Equals(FirmwareClassGuid, StringComparison.OrdinalIgnoreCase);
            if (!signature.Equals("$Windows NT$", StringComparison.OrdinalIgnoreCase) || className.Length == 0 ||
                (requireFirmware ? !firmwareClass || !firmwareGuid : firmwareClass || firmwareGuid) ||
                !sections.TryGetValue("Manufacturer", out List<string>? manufacturers))
            {
                return false;
            }

            HashSet<string> ids = new(hardwareIds.Where(static id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            bool matched = false;
            foreach (string manufacturer in manufacturers)
            {
                string[] fields = ReadFields(Assignment(manufacturer).Value, strings);
                if (fields.Length < 2 || fields[0].Length == 0)
                {
                    continue;
                }

                (int Major, int Minor, int Build, int Product)? bestRank = null;
                string? selectedSection = null;
                foreach (string decoration in fields.Skip(1))
                {
                    var rank = GetTargetRank(decoration, architecture, target.BuildMajor);
                    if (rank is null || bestRank is not null && rank.Value.CompareTo(bestRank.Value) <= 0)
                    {
                        continue;
                    }
                    bestRank = rank;
                    selectedSection = $"{fields[0]}.{decoration}";
                }

                // An empty newer section intentionally removes support: never fall back to an older matching row.
                if (selectedSection is null || !sections.TryGetValue(selectedSection, out List<string>? models))
                {
                    continue;
                }
                foreach (string model in models)
                {
                    string[] modelFields = ReadFields(Assignment(model).Value, strings);
                    if (modelFields.Length >= 2 && modelFields[0].Length > 0 && modelFields.Skip(1).Any(ids.Contains))
                    {
                        matched = true;
                    }
                }
            }
            return matched;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static (int Major, int Minor, int Build, int Product)? GetTargetRank(string decoration, string architecture, int targetBuild)
    {
        string[] parts = decoration.Split('.');
        if (parts.Length > 6 || !parts[0].StartsWith("NT", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Unsupported INF target decoration.");
        }
        if (!parts[0].Equals(architecture, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int major = Number(parts, 1);
        int minor = Number(parts, 2);
        int product = Number(parts, 3);
        int suite = Number(parts, 4);
        int build = Number(parts, 5);
        if (major > 10 || major == 10 && minor > 0 || product is not (0 or 1))
        {
            return null;
        }
        if (suite != 0)
        {
            // No target suite mask is available. Ignoring this section could incorrectly select an older section.
            throw new FormatException("The INF requires unknown target suite facts.");
        }
        if (build > 0 && (major != 10 || minor != 0 || build < 14310))
        {
            throw new FormatException("Invalid build-specific INF target decoration.");
        }
        if (major == 10 && minor == 0 && build > targetBuild)
        {
            return null;
        }
        return (major, minor, build, product);
    }

    private static int Number(string[] fields, int index)
    {
        if (index >= fields.Length || fields[index].Length == 0)
        {
            return 0;
        }
        string value = fields[index];
        bool hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(hexadecimal ? value[2..] : value,
            hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out int result) || result < 0)
        {
            throw new FormatException("Invalid INF target number.");
        }
        return result;
    }

    private static Dictionary<string, List<string>> ReadSections(string content)
    {
        Dictionary<string, List<string>> sections = new(StringComparer.OrdinalIgnoreCase);
        using StringReader reader = new(content.TrimStart('\uFEFF'));
        List<string>? current = null;
        StringBuilder continuation = new();
        while (reader.ReadLine() is { } physicalLine)
        {
            if (physicalLine.Any(static ch => char.IsControl(ch) && ch != '\t'))
            {
                throw new FormatException("Invalid INF control character.");
            }
            string line = Split(physicalLine, ';')[0].Trim();
            bool continued = line.EndsWith('\\');
            continuation.Append(continued ? line[..^1] : line);
            if (continuation.Length > 64 * 1024)
            {
                throw new FormatException("INF logical line is too large.");
            }
            if (continued)
            {
                continue;
            }
            line = continuation.ToString();
            continuation.Clear();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Length is < 3 or > 257)
                {
                    throw new FormatException("Invalid INF section.");
                }
                string name = line[1..^1];
                if (!sections.TryGetValue(name, out current))
                {
                    current = [];
                    sections.Add(name, current);
                }
            }
            else if (current is not null)
            {
                current.Add(line);
            }
            else
            {
                throw new FormatException("INF entry has no section.");
            }
        }
        if (continuation.Length > 0)
        {
            throw new FormatException("Incomplete INF continuation.");
        }
        return sections;
    }

    private static Dictionary<string, string> ReadStrings(Dictionary<string, List<string>> sections)
    {
        Dictionary<string, string> strings = new(StringComparer.OrdinalIgnoreCase);
        if (sections.TryGetValue("Strings", out List<string>? entries))
        {
            foreach (string entry in entries)
            {
                var assignment = Assignment(entry);
                string value = Unquote(assignment.Value);
                if (!strings.TryAdd(assignment.Key, value) && strings[assignment.Key] != value)
                {
                    throw new FormatException("Conflicting INF strings.");
                }
            }
        }

        // Windows may use a localized Strings section. Proof fields must not change with its selected locale;
        // untranslated manufacturer/description labels are not expanded by this applicability check.
        HashSet<string> localizedVariants = new(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections.Where(static section => section.Key.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase)))
        {
            Dictionary<string, string> localized = new(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in section.Value)
            {
                var assignment = Assignment(entry);
                if (!localized.TryAdd(assignment.Key, Unquote(assignment.Value)))
                {
                    throw new FormatException("Conflicting localized INF strings.");
                }
            }
            foreach (var invariant in strings)
            {
                if (!localized.TryGetValue(invariant.Key, out string? translation) ||
                    !invariant.Value.Equals(translation, StringComparison.OrdinalIgnoreCase))
                {
                    localizedVariants.Add(invariant.Key);
                }
            }
        }
        foreach (string key in localizedVariants)
        {
            strings.Remove(key);
        }
        return strings;
    }

    private static string ReadDirective(Dictionary<string, List<string>> sections, Dictionary<string, string> strings, string name)
    {
        string? value = null;
        if (sections.TryGetValue("Version", out List<string>? entries))
        {
            foreach (string entry in entries)
            {
                var assignment = Assignment(entry);
                if (!assignment.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string[] fields = ReadFields(assignment.Value, strings);
                if (fields.Length != 1 || value is not null && !value.Equals(fields[0], StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("Ambiguous INF version directive.");
                }
                value = fields[0];
            }
        }
        return value ?? string.Empty;
    }

    private static (string Key, string Value) Assignment(string line)
    {
        string[] parts = Split(line, '=');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
        {
            throw new FormatException("Invalid INF assignment.");
        }
        return (Unquote(parts[0]), parts[1]);
    }

    private static string[] ReadFields(string value, Dictionary<string, string> strings) =>
        Split(value, ',').Select(field => Expand(Unquote(field), strings, 0)).ToArray();

    private static string Expand(string value, Dictionary<string, string> strings, int depth)
    {
        if (depth > 8 || value.Length > MaximumFieldCharacters)
        {
            throw new FormatException("INF string expansion is too large or recursive.");
        }
        StringBuilder expanded = new();
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                expanded.Append(value[index]);
                continue;
            }
            int end = value.IndexOf('%', index + 1);
            if (end < 0)
            {
                throw new FormatException("Unterminated INF string token.");
            }
            string key = value[(index + 1)..end];
            if (key.Length == 0)
            {
                expanded.Append('%');
            }
            else if (strings.TryGetValue(key, out string? replacement))
            {
                expanded.Append(Expand(replacement, strings, depth + 1));
            }
            else
            {
                throw new FormatException("Unresolved INF string token.");
            }
            index = end;
            if (expanded.Length > MaximumFieldCharacters)
            {
                throw new FormatException("INF expanded field is too large.");
            }
        }
        return expanded.Length <= MaximumFieldCharacters ? expanded.ToString() : throw new FormatException("INF field is too large.");
    }

    private static string Unquote(string field)
    {
        string value = field.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
        {
            value = value[1..^1].Replace("\"\"", "\"");
        }
        else if (value.Contains('"'))
        {
            throw new FormatException("Unsupported INF quoted field.");
        }
        return value.Length <= MaximumFieldCharacters ? value : throw new FormatException("INF field is too large.");
    }

    private static string[] Split(string value, char separator)
    {
        List<string> fields = [];
        bool quoted = false;
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                if (quoted && index + 1 < value.Length && value[index + 1] == '"')
                {
                    index++;
                    continue;
                }
                quoted = !quoted;
            }
            if (!quoted && value[index] == separator)
            {
                fields.Add(value[start..index]);
                start = index + 1;
                if (separator == ';')
                {
                    return fields.ToArray();
                }
            }
        }
        if (quoted)
        {
            throw new FormatException("Unterminated INF quoted field.");
        }
        fields.Add(value[start..]);
        return fields.ToArray();
    }
}
