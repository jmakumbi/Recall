using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Recall.Ingestion;

/// <summary>
/// BCL-only fallback text extractor for Office Open XML formats (.docx, .xlsx, .pptx)
/// and plain text files. Used when IFilter is unavailable or times out.
///
/// Office Open XML files are ZIP archives containing XML parts. This extractor
/// reads the relevant XML parts and strips markup using a lightweight regex,
/// requiring no third-party file-format libraries.
/// </summary>
public sealed class OpenXmlExtractor
{
    private static readonly Regex XmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    private readonly IngestionConfig _config;

    public OpenXmlExtractor(IngestionConfig config) => _config = config;

    /// <summary>
    /// Extract plain text from the given file.
    /// Supports: .docx, .xlsx, .pptx (Open XML), .txt, .md, .csv, .log.
    /// Returns null for unsupported extensions.
    /// Never throws to caller.
    /// </summary>
    public string? Extract(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".docx" or ".doc"  => ExtractDocx(filePath),
                ".xlsx" or ".xls"  => ExtractXlsx(filePath),
                ".pptx" or ".ppt"  => ExtractPptx(filePath),
                ".txt"  or ".md"
                        or ".csv"
                        or ".log"
                        or ".ini"  => ExtractPlainText(filePath),
                _                  => null
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[recall] OpenXmlExtractor failed for '{filePath}': {ex.Message}");
            return null;
        }
    }

    // ── .docx — word/document.xml ─────────────────────────────────────────────

    private string? ExtractDocx(string filePath)
    {
        using var zip = OpenZip(filePath);
        if (zip is null) return null;

        var sb = new StringBuilder();

        // Main document body
        AppendXmlPart(zip, "word/document.xml", sb);

        // Headers and footers (optional)
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase))
                AppendXmlPart(zip, entry.FullName, sb);
        }

        return Finalise(sb);
    }

    // ── .xlsx — xl/sharedStrings.xml + worksheets ─────────────────────────────

    private string? ExtractXlsx(string filePath)
    {
        using var zip = OpenZip(filePath);
        if (zip is null) return null;

        var sb = new StringBuilder();

        // Shared strings (most text lives here)
        AppendXmlPart(zip, "xl/sharedStrings.xml", sb);

        // Each worksheet for inline cell values
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                AppendXmlPart(zip, entry.FullName, sb);

            if (sb.Length >= _config.MaxExtractedCharsPerFile) break;
        }

        return Finalise(sb);
    }

    // ── .pptx — ppt/slides/slide*.xml ────────────────────────────────────────

    private string? ExtractPptx(string filePath)
    {
        using var zip = OpenZip(filePath);
        if (zip is null) return null;

        var sb = new StringBuilder();

        var slides = zip.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in slides)
        {
            AppendXmlPart(zip, entry.FullName, sb);
            if (sb.Length >= _config.MaxExtractedCharsPerFile) break;
        }

        return Finalise(sb);
    }

    // ── Plain text ────────────────────────────────────────────────────────────

    private string? ExtractPlainText(string filePath)
    {
        var text = File.ReadAllText(filePath);
        return text.Length > _config.MaxExtractedCharsPerFile
            ? text[.._config.MaxExtractedCharsPerFile]
            : text.Length > 0 ? text : null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ZipArchive? OpenZip(string filePath)
    {
        try   { return ZipFile.OpenRead(filePath); }
        catch { return null; }  // not a valid ZIP (e.g. legacy .doc binary)
    }

    private void AppendXmlPart(ZipArchive zip, string entryName, StringBuilder sb)
    {
        if (sb.Length >= _config.MaxExtractedCharsPerFile) return;

        var entry = zip.GetEntry(entryName);
        if (entry is null) return;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var xml  = reader.ReadToEnd();
        var text = XmlTagRegex.Replace(xml, " ");      // strip XML tags
        text     = WhitespaceRegex.Replace(text, " "); // collapse whitespace
        text     = text.Trim();

        if (text.Length > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            var remaining = _config.MaxExtractedCharsPerFile - sb.Length;
            sb.Append(text.Length > remaining ? text[..remaining] : text);
        }
    }

    private string? Finalise(StringBuilder sb)
    {
        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }
}
