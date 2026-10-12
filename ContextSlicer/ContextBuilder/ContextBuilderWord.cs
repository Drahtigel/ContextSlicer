using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer.ContextBuilder;

public class ContextBuilderWord : ContextBuilderBase
{
    public ContextBuilderWord(string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages)
        : base(promptRules, moduleRules, includeDirectoryStructure, includeImages) { }

    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        var sb = new StringBuilder();
        if (savedEntries == null || savedEntries.Count == 0) return sb;

        string docxPath = checkedFiles?.FirstOrDefault()?.FullPath
            ?? savedEntries.FirstOrDefault(e => e != null && !string.IsNullOrEmpty(e.FilePath))?.FilePath
            ?? string.Empty;

        if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath)) return sb;

        AppendSystemRules(sb);

        var selectedPathsSet = new HashSet<string>(
            savedEntries.Where(e => e != null).Select(e => e.EntryPath),
            StringComparer.OrdinalIgnoreCase);

        if (IncludeDirectoryStructure)
        {
            sb.AppendLine("<document_structure>");
            sb.AppendLine("  <ul id=\"toc_root\">");
            foreach (var entry in savedEntries.Where(e => e != null))
            {
                string elementId = "h_" + unchecked((uint)entry.EntryPath.GetHashCode()).ToString("x8");
                int currentLevel = entry.EntryPath.Count(f => f == '/');
                sb.AppendLine($"    <li level=\"{currentLevel}\" type=\"heading\" id=\"{elementId}\">{entry.DisplayName}</li>");
            }
            sb.AppendLine("  </ul id=\"toc_root\">");
            sb.AppendLine("</document_structure>\n");
        }

        await ProcessWordBodyElementsInternalAsync(sb, docxPath, selectedPathsSet, token);
        return sb;
    }

    private async Task ProcessWordBodyElementsInternalAsync(
        StringBuilder sb, string docxPath, HashSet<string> selectedPathsSet, CancellationToken token)
    {
        await Task.Run(() =>
        {
            try
            {
                using (var wordDoc = WordprocessingDocument.Open(docxPath, false))
                {
                    var mainPart = wordDoc.MainDocumentPart;
                    if (mainPart?.Document?.Body == null) return;

                    var bodyElements = mainPart.Document.Body.ChildElements;
                    var approvedHeadingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var entryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var path in selectedPathsSet)
                    {
                        if (string.IsNullOrEmpty(path)) continue;
                        string pureName = path.Split('/').LastOrDefault()?.Trim().TrimEnd('.') ?? string.Empty;
                        if (!string.IsNullOrEmpty(pureName))
                        {
                            approvedHeadingNames.Add(pureName);
                            entryMap[pureName] = path;
                        }
                    }

                    bool isCurrentParagraphApproved = false;
                    string activeElementId = string.Empty;

                    foreach (var element in bodyElements)
                    {
                        token.ThrowIfCancellationRequested();

                        if (element is Paragraph paragraph)
                        {
                            var textNodes = paragraph.Descendants<Text>().ToList();
                            string pText = string.Concat(textNodes.Select(t => t.Text ?? string.Empty))
                                             .Replace("\r", "").Replace("\n", "").Replace("\u00A0", " ");

                            string styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
                            string cleanStyleId = styleId.Replace(" ", "");

                            bool isHeading = !string.IsNullOrEmpty(cleanStyleId) &&
                                             (cleanStyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                                              cleanStyleId.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase));

                            if (isHeading)
                            {
                                string headingText = pText.Trim();
                                string cleanHeadingName = headingText.TrimEnd('.');

                                if (!string.IsNullOrEmpty(cleanHeadingName))
                                {
                                    if (isCurrentParagraphApproved && !string.IsNullOrEmpty(activeElementId))
                                    {
                                        sb.AppendLine($"</heading id=\"{activeElementId}\">");
                                        activeElementId = string.Empty;
                                    }

                                    if (approvedHeadingNames.Contains(cleanHeadingName))
                                    {
                                        isCurrentParagraphApproved = true;
                                        entryMap.TryGetValue(cleanHeadingName, out string? fullPath);
                                        string finalPath = fullPath ?? cleanHeadingName;
                                        activeElementId = "h_" + unchecked((uint)finalPath.GetHashCode()).ToString("x8");

                                        sb.AppendLine($"<heading id=\"{activeElementId}\" path=\"{finalPath}\">");
                                        sb.AppendLine($"  {headingText}");
                                    }
                                    else
                                    {
                                        isCurrentParagraphApproved = false;
                                        activeElementId = string.Empty;
                                    }
                                }
                            }
                            else if (isCurrentParagraphApproved && !string.IsNullOrWhiteSpace(pText))
                            {
                                sb.AppendLine($"  {pText.Trim()}");
                            }
                        }
                        // В) ОБРАБОТКА ТАБЛИЦ ВНУТРИ ОДОБРЕННЫХ ГЛАВ WORD
                        else if (element is Table table)
                        {
                            if (isCurrentParagraphApproved)
                            {
                                sb.AppendLine("  <table>");
                                foreach (var row in table.Descendants<TableRow>())
                                {
                                    sb.AppendLine("    <tr>");
                                    foreach (var cell in row.Descendants<TableCell>())
                                    {
                                        string cellText = string.Concat(cell.Descendants<Text>().Select(t => t.Text))
                                                                .Replace("\r", "")
                                                                .Replace("\n", "")
                                                                .Trim();
                                        sb.AppendLine($"      <td>{cellText}</td>");
                                    }
                                    sb.AppendLine("    </tr>");
                                }
                                sb.AppendLine("  </table>");
                            }
                        }
                    }

                    // Закрываем самую последнюю главу романа перед выходом из файла
                    if (isCurrentParagraphApproved && !string.IsNullOrEmpty(activeElementId))
                    {
                        sb.AppendLine($"</heading id=\"{activeElementId}\">");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Word OOP Body Iterator Error] {ex.Message}");
            }
        }, token);
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: ПОЛНОЦЕННЫЙ НАЛИВ СТРОК В ИЗДАТЕЛЬСКИЙ PDF ДЛЯ WORD   -->
    // ================================================================= -->
    public override async Task GeneratePdfAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);
        string metaDataText = sb.ToString();
        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style != null) { style.Font.Name = "Arial"; style.Font.Size = 10; }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Построчно читаем буфер романа и наливаем параграфы MigraDoc
        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                var p = currentSection.AddParagraph();
                p.AddFormattedText(line);

                if (string.IsNullOrWhiteSpace(line))
                {
                    p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
                }
            }
        }

        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    public override Task GenerateDocxAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
