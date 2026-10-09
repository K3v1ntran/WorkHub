using System.Xml;
using System.Xml.Linq;
using WorkHub.Core.LegacyImport;

namespace WorkHub.Infrastructure.LegacyImport;

/// <summary>
/// Reads the legacy <c>HubData</c> XML as untrusted input:
/// DTDs are prohibited (so no internal/external entities, no entity expansion), no <see cref="XmlResolver"/> is used
/// (nothing is fetched from disk or network), and size is capped. Only <c>HubData/SYSTEM|ORACLE/Item</c> with the
/// <c>SystemCode</c> / <c>SystemName</c> attributes is read; other elements are ignored and counted.
/// </summary>
public sealed class LegacyXmlParser : ILegacyXmlParser
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = MaxFileBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = true,
    };

    public LegacyParseResult Parse(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return LegacyParseResult.Failed("The file could not be found.");
            if (file.Length == 0)
                return LegacyParseResult.Failed("The file is empty.");
            if (file.Length > MaxFileBytes)
                return LegacyParseResult.Failed("The file is too large to be a legacy WorkHub XML file.");

            return Parse(file.OpenRead());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LegacyParseResult.Failed($"The file could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>Parses XML from a stream (for example the embedded initial data) with the same protections. Closes the stream.</summary>
    public LegacyParseResult Parse(Stream stream)
    {
        try
        {
            using var reader = XmlReader.Create(stream, ReaderSettings);
            return Read(XDocument.Load(reader, LoadOptions.SetLineInfo));
        }
        catch (XmlException ex) when (ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyParseResult.Failed("The file contains a DTD, which is not allowed in a legacy WorkHub XML file.");
        }
        catch (XmlException ex)
        {
            return LegacyParseResult.Failed($"The file is not valid XML (line {ex.LineNumber}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LegacyParseResult.Failed($"The file could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>Exposed for tests that build documents in memory.</summary>
    internal static LegacyParseResult Read(XDocument document)
    {
        var root = document.Root;
        if (root is null || root.Name.LocalName != "HubData" || root.Name.Namespace != XNamespace.None)
            return LegacyParseResult.Failed("This is not a legacy WorkHub file (the root element must be <HubData>).");

        var entries = new List<LegacyEntry>();
        var ignored = 0;
        foreach (var section in root.Elements())
        {
            LegacySection? kind = section.Name.LocalName switch
            {
                "SYSTEM" when section.Name.Namespace == XNamespace.None => LegacySection.System,
                "ORACLE" when section.Name.Namespace == XNamespace.None => LegacySection.Oracle,
                _ => null,
            };
            if (kind is null)
            {
                ignored++;
                continue;
            }

            foreach (var element in section.Elements())
            {
                if (element.Name != "Item")
                {
                    ignored++;
                    continue;
                }

                var line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : (int?)null;
                entries.Add(new LegacyEntry(kind.Value, (string?)element.Attribute("SystemCode"), (string?)element.Attribute("SystemName"), line));
            }
        }

        return new LegacyParseResult(entries, null, ignored);
    }
}
