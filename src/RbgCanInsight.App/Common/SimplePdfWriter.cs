using System.Globalization;
using System.IO;
using System.Text;

namespace RbgCanInsight.App.Common;

/// <summary>Small dependency-free PDF text writer used for portable offline reports. It emits
/// standard Helvetica pages and intentionally contains no active content or external links.</summary>
public static class SimplePdfWriter
{
    public static byte[] Create(string title, IEnumerable<string> sourceLines)
    {
        var lines = sourceLines.SelectMany(line => Wrap(ToAscii(line), 96)).ToList();
        title = ToAscii(title);
        const int linesPerPage = 44;
        var pages = lines.Chunk(linesPerPage).ToArray();
        if (pages.Length == 0) pages = [[]];

        var objects = new SortedDictionary<int, byte[]>();
        objects[1] = Bytes("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(' ', Enumerable.Range(0, pages.Length).Select(index => $"{4 + index * 2} 0 R"));
        objects[2] = Bytes($"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");
        objects[3] = Bytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (var index = 0; index < pages.Length; index++)
        {
            var pageObject = 4 + index * 2;
            var contentObject = pageObject + 1;
            objects[pageObject] = Bytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObject} 0 R >>");
            var content = new StringBuilder("BT /F1 14 Tf 46 798 Td ")
                .Append('(').Append(Escape(title)).Append(") Tj ")
                .Append("/F1 10 Tf 0 -24 Td 14 TL ");
            foreach (var line in pages[index])
            {
                content.Append('(').Append(Escape(line)).Append(") Tj T* ");
            }
            content.Append("ET BT /F1 8 Tf 500 24 Td ")
                .Append('(').Append($"Page {index + 1} of {pages.Length}").Append(") Tj ET");
            var contentBytes = Bytes(content.ToString());
            objects[contentObject] = Bytes($"<< /Length {contentBytes.Length} >>\nstream\n")
                .Concat(contentBytes).Concat(Bytes("\nendstream")).ToArray();
        }

        using var stream = new MemoryStream();
        Write(stream, "%PDF-1.4\n%RBG-CAN-Insight\n");
        var offsets = new Dictionary<int, long>();
        foreach (var pair in objects)
        {
            offsets[pair.Key] = stream.Position;
            Write(stream, $"{pair.Key} 0 obj\n");
            stream.Write(pair.Value);
            Write(stream, "\nendobj\n");
        }

        var xref = stream.Position;
        Write(stream, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var number in Enumerable.Range(1, objects.Count))
        {
            Write(stream, offsets[number].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        }
        Write(stream, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        var lines = new List<string>();
        while (text.Length > width)
        {
            var split = text.LastIndexOf(' ', width);
            if (split < width / 2) split = width;
            lines.Add(text[..split]);
            text = text[split..].TrimStart();
        }
        lines.Add(text);
        return lines;
    }

    private static string ToAscii(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(character switch
            {
                >= ' ' and <= '~' => character.ToString(),
                '\u00B2' => "2",
                '\u00B0' => " deg",
                '\u00B7' or '\u2022' => " | ",
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => "-",
                '\u2192' => "->",
                '\u2194' => "<->",
                '\u2264' => "<=",
                '\u2265' => ">=",
                _ => " "
            });
        }
        return result.ToString();
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
    private static void Write(Stream stream, string value) => stream.Write(Bytes(value));
}
