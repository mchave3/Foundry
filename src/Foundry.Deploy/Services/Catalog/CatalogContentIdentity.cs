// Copyright (c) Foundry Project contributors.
// Licensed under the MIT License.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Foundry.Deploy.Services.Catalog;

/// <summary>Fingerprints already-authenticated bounded catalog content; hashing alone does not establish trust.</summary>
internal static class CatalogContentIdentity
{
    private const int MaximumBytes = 32 * 1024 * 1024;

    public static string Calculate(string content)
    {
        if (content.Length > MaximumBytes || Encoding.UTF8.GetByteCount(content) > MaximumBytes)
        {
            throw new InvalidDataException("Catalog metadata exceeds the 32 MiB limit.");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    public static XDocument ParseXml(string content)
    {
        using var text = new StringReader(content);
        using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumBytes
        });
        return XDocument.Load(reader);
    }
}
