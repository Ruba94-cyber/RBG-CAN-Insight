using System.Xml;

namespace RbgCanInsight.DiagData;

/// <summary>
/// XDocument.Load is super-linear in element nesting depth, so a hostile or corrupt file with
/// 100,000 nested elements can hang the importer for minutes. A linear streaming pre-pass rejects
/// such documents before the tree is built.
/// </summary>
internal static class XmlDepthGuard
{
    internal const int MaxDepth = 512;

    internal static void Check(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.Depth > MaxDepth)
                throw new InvalidDataException(
                    $"The XML document is nested deeper than {MaxDepth} levels and was rejected as unsafe.");
        }
    }
}
