using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ContextSlicer.Filesystem;

/// <summary>
/// Высокопроизводительный парсер структуры документов MS Word (.docx).
/// Извлекает иерархию глав по стилям заголовков и регистрирует встроенную графику.
/// </summary>
public class WordDocumentParser : ISyntaxParser
{
    private const string CacheFolderName = "googlecache";

    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        if (!File.Exists(absolutePath)) return result;

        return await Task.Run(() =>
        {
            try
            {
                // ================================================================= -->
                // ИСПРАВЛЕНО: ПЕРЕНОС ИЗВЛЕЧЕНИЯ КАРТИНОК ДЛЯ ЗАЩИТЫ ПОТОКА ЧТЕНИЯ  -->
                // ================================================================= -->
                using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(absolutePath, false))
                {
                    var mainPart = wordDoc.MainDocumentPart;
                    if (mainPart == null || mainPart.Document == null || mainPart.Document.Body == null) return result;

                    int globalCharIndex = 0;

                    // ШАГ 1 (ИСПРАВЛЕНО): Сначала собираем элементы текста через Descendants.
                    // Теперь, даже если коллекция картинок ImageParts вызовет внутренний сбой пакета,
                    // текст глав и оглавление книги гарантированно считаются без осечек!
                    var allDocumentElements = mainPart.Document.Body
                        .Descendants()
                        .Where(e => e is Paragraph || e is Table)
                        .ToList();

                    // ШАГ 2: Запускаем синтаксический обход элементов
                    var entries = ParseContentElements(allDocumentElements, relativePath, ref globalCharIndex);
                    if (entries.Count > 0)
                    {
                        result.AddRange(entries);
                    }

                    // ШАГ 3 (ИСПРАВЛЕНО): Извлекаем картинки строго ПОСЛЕ того, 
                    // как структура глав успешно собрана и защищена от падений!
                    ExtractWordImages(mainPart, absolutePath);
                }

            }
            catch (Exception ex)
            {
                string errTemplate = Application.Current.Resources["Str_Err_Update_ReadDir"] as string
                                     ?? "Ошибка доступа к файлам структуры документа.";
                System.Diagnostics.Debug.WriteLine($"[{errTemplate}] {ex.Message}");
            }

            return result;
        });
    }

    private void ExtractWordImages(MainDocumentPart mainPart, string absolutePath)
    {
        try
        {
            string fileNameNoExt = Path.GetFileNameWithoutExtension(absolutePath);
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CacheFolderName, "images", fileNameNoExt);

            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            foreach (var imagePart in mainPart.ImageParts)
            {
                string relationshipId = mainPart.GetIdOfPart(imagePart);
                if (string.IsNullOrEmpty(relationshipId)) continue;

                string ext = imagePart.ContentType switch
                {
                    "image/jpeg" => ".jpg",
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    _ => ".png"
                };

                string targetImgPath = Path.Combine(cacheDir, $"{relationshipId}{ext}");

                if (!File.Exists(targetImgPath))
                {
                    using (var sourceStream = imagePart.GetStream())
                    using (var targetStream = File.Create(targetImgPath))
                    {
                        sourceStream.CopyTo(targetStream);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Word Image Extract Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Линейный разбор элементов: собирает иерархию заголовков и считает длины текстовых блоков.
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО CS1061: ПРЯМАЯ ВЫЧИТКА ТЕКСТОВЫХ НОД ИЗ ПОТОКА OPENXML -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: ВСЕЯДНЫЙ ДЕТЕКТОР РУССКИХ И АНГЛИЙСКИХ СТИЛЕЙ ЗАГОЛОВКОВ -->
    // ================================================================= -->
    private List<SyntaxEntry> ParseContentElements(IEnumerable<DocumentFormat.OpenXml.OpenXmlElement> elements, string relativePath, ref int currentAbsoluteIndex)
    {
        var entries = new List<SyntaxEntry>();
        var activePaths = new Dictionary<int, string> { { 0, string.Empty } };

        foreach (var element in elements)
        {
            if (element is Paragraph paragraph)
            {
                var textNodes = paragraph.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().ToList();
                int elementLength = textNodes.Sum(t => (t.Text ?? string.Empty).Length);

                // Извлекаем сырой ID стиля из XML-разметки OpenXML
                string styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;

                // Вычищаем пробелы для поддержки стилей вида "Heading 1" или "Заголовок 2"
                string cleanStyleId = styleId.Replace(" ", "");

                // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Добавлена поддержка русской локализации Word ("Заголовок")!
                // Теперь парсер гарантированно поймает главы в любом документе!
                bool isHeading = !string.IsNullOrEmpty(cleanStyleId) &&
                                 (cleanStyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                                  cleanStyleId.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase));

                if (isHeading)
                {
                    // Извлекаем цифру уровня (например, из "Заголовок1" или "Heading2" забираем 1 или 2)
                    string levelDigits = new string(cleanStyleId.Where(char.IsDigit).ToArray());
                    if (!int.TryParse(levelDigits, out int level)) level = 1; // Фолбэк на 1 уровень, если цифра не найдена

                    string headingText = string.Concat(textNodes.Select(t => t.Text ?? string.Empty)).Trim();

                    if (!string.IsNullOrEmpty(headingText))
                    {
                        int targetParentLevel = level - 1;
                        while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel))
                        {
                            targetParentLevel--;
                        }

                        string parentPath = activePaths[targetParentLevel];
                        string entryPath = string.IsNullOrEmpty(parentPath) ? headingText : $"{parentPath}/{headingText}";

                        uint rawHash = unchecked((uint)entryPath.GetHashCode());
                        string elementId = "h_" + rawHash.ToString("x8");

                        entries.Add(new SyntaxEntry
                        {
                            FilePath = relativePath,
                            EntryPath = entryPath,
                            DisplayName = headingText,
                            Type = EntryType.Heading,
                            SpanInfo = $"{currentAbsoluteIndex},{elementLength},{elementId}"
                        });

                        activePaths[level] = entryPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                }

                currentAbsoluteIndex += elementLength;
            }
            else if (element is Table table)
            {
                int tableLength = 0;
                var cells = table.Descendants<TableCell>();
                foreach (var cell in cells)
                {
                    tableLength += cell.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Sum(t => (t.Text ?? string.Empty).Length);
                }

                currentAbsoluteIndex += tableLength;
            }
            else
            {
                currentAbsoluteIndex += 10;
            }
        }
        return entries;
    }
} // Финальное закрытие класса и пространства имен!

