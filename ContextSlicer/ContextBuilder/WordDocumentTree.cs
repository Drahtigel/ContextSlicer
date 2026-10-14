using ContextSlicer.Filesystem;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Реализация объектного дерева документа для локального формата MS Word (.docx).
/// Транслирует линейные элементы OpenXML в универсальные иерархические узлы DocEntry.
/// </summary>
public class WordDocumentTree : BaseDocumentTree
{

    public WordDocumentTree(string cacheDirectory, string documentId) : base(cacheDirectory, documentId) { }
    /// <summary>
    /// Главная точка входа: открывает пакет OpenXML .docx файла и запускает итератор элементов.
    /// </summary>
    public override void ParseToTree(string sourcePath, List<SyntaxEntry> savedEntries)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || savedEntries == null || savedEntries.Count == 0)
            return;

        try
        {
            this.DocTitle = Path.GetFileNameWithoutExtension(sourcePath);

            using (var wordDoc = WordprocessingDocument.Open(sourcePath, false))
            {
                var mainPart = wordDoc.MainDocumentPart;
                if (mainPart?.Document?.Body == null) return;

                var bodyElements = mainPart.Document.Body.ChildElements;
                var checkList = savedEntries.ToList();

                // Создаем виртуальную корневую вкладку, имитирующую имя файла,
                // чтобы выравнивание путей прозы полностью совпадало с деревом Google Docs
                var virtualRootTab = new DocEntry
                {
                    EntryTitle = this.DocTitle,
                    EntryPath = this.DocTitle,
                    Type = DocEntryType.Tab,
                    HeadingLevel = 0
                };
                this.ChildEntries.Add(virtualRootTab);

                // Запускаем основной конвейер трансляции элементов OpenXML
                ParseWordElementsToTree(bodyElements, virtualRootTab, checkList, this.DocTitle);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Word DOM Parse Error] {ex.Message}");
        }
    }
    /// <summary>
    /// Линейно обходит элементы OpenXML (параграфы, таблицы), сопоставляет их с UI-галочками
    /// и выстраивает иерархическую матрешку DocEntry.
    /// </summary>
    private void ParseWordElementsToTree(
        IEnumerable<DocumentFormat.OpenXml.OpenXmlElement> bodyElements,
        DocEntry parentTab,
        List<SyntaxEntry> savedEntries,
        string tabPath)
    {
        var activeChain = new List<DocEntry> { parentTab };
        var activePaths = new Dictionary<int, string> { { 0, tabPath } };
        DocEntry? currentActiveTarget = parentTab;

        bool isInListMode = false;
        string currentListId = "l_generic";

        foreach (var element in bodyElements)
        {
            if (element == null) continue;

            // А) ОБРАБОТКА АБЗАЦЕВ И ЗАГОЛОВКОВ MS WORD
            if (element is Paragraph paragraph)
            {
                var textNodes = paragraph.Descendants<Text>().ToList();
                string pText = string.Concat(textNodes.Select(t => t.Text ?? string.Empty))
                                 .Replace("\r", "")
                                 .Replace("\n", "")
                                 .Replace("\u00A0", " ");

                string styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? string.Empty;
                string cleanStyleId = styleId.Replace(" ", "");

                // Распознаем стили заголовков (Heading1-6, Заголовок1-6, Title, Subtitle)
                string levelDigits = new string(cleanStyleId.Where(char.IsDigit).ToArray());
                bool hasLevel = int.TryParse(levelDigits, out int level);
                if (!hasLevel) level = 1;

                bool isHeadingStyle = !string.IsNullOrEmpty(cleanStyleId) &&
                                     (cleanStyleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                                      cleanStyleId.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase));

                bool isTitle = cleanStyleId.StartsWith("Title", StringComparison.OrdinalIgnoreCase) ||
                               cleanStyleId.StartsWith("Название", StringComparison.OrdinalIgnoreCase);

                bool isSubtitle = cleanStyleId.StartsWith("Subtitle", StringComparison.OrdinalIgnoreCase) ||
                                  cleanStyleId.StartsWith("Подзаголовок", StringComparison.OrdinalIgnoreCase);

                if (isHeadingStyle || isTitle || isSubtitle)
                {
                    if (isInListMode && currentActiveTarget != null)
                    {
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">";
                        isInListMode = false;
                    }

                    string headingTitle = pText.Trim();
                    if (string.IsNullOrEmpty(headingTitle)) continue;

                    int targetLevel = isHeadingStyle ? level : 1;

                    while (activeChain.Count > 1 && activeChain[activeChain.Count - 1].HeadingLevel >= targetLevel)
                    {
                        activeChain.RemoveAt(activeChain.Count - 1);
                    }

                    DocEntry correctParent = activeChain[activeChain.Count - 1];
                    int targetParentLevel = targetLevel - 1;
                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel)) targetParentLevel--;

                    string parentPath = activePaths[targetParentLevel];
                    string cleanHeadingName = headingTitle.TrimEnd('.');
                    string fullHeadingPath = string.IsNullOrEmpty(parentPath) ? cleanHeadingName : $"{parentPath}/{cleanHeadingName}";

                    var match = savedEntries.FirstOrDefault(e =>
                        (e.Type == EntryType.Heading || e.Type == EntryType.Section) &&
                        e.EntryPath.Trim().TrimEnd('.').Equals(fullHeadingPath, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        DocEntryType localType = DocEntryType.Heading;
                        if (isTitle) localType = DocEntryType.Title;
                        else if (isSubtitle) localType = DocEntryType.Subtitle;

                        var headingEntry = new DocEntry
                        {
                            EntryTitle = headingTitle,
                            EntryPath = match.EntryPath,
                            Type = localType,
                            HeadingLevel = targetLevel
                        };

                        savedEntries.Remove(match);
                        correctParent.ChildEntries.Add(headingEntry);
                        activeChain.Add(headingEntry);
                        currentActiveTarget = headingEntry;

                        activePaths[targetLevel] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > targetLevel).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                    else
                    {
                        currentActiveTarget = null;
                        activePaths[targetLevel] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > targetLevel).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                }
                // ОБРАБОТКА СПИСКОВ ВНУТРИ WORD (Наличие свойств нумерации/маркеров)
                else if (currentActiveTarget != null && paragraph.ParagraphProperties?.NumberingProperties != null)
                {
                    string itemText = pText.Trim();
                    if (string.IsNullOrEmpty(itemText)) continue;

                    int nestingLevel = paragraph.ParagraphProperties.NumberingProperties.NumberingLevelReference?.Val?.Value ?? 0;

                    if (!isInListMode)
                    {
                        int hash = 0;
                        string seed = itemText + tabPath;
                        unchecked
                        {
                            foreach (char c in seed) hash = (hash * 31) + c;
                        }
                        currentListId = $"l_{Math.Abs(hash).ToString("x4")}";

                        currentActiveTarget.EntryContent += $"{Environment.NewLine}<ul id=\"{currentListId}\">";
                        isInListMode = true;
                    }

                    currentActiveTarget.EntryContent += $"{Environment.NewLine}  <li level=\"{nestingLevel}\">{itemText}</li>";
                }
                // ОБРАБОТКА ОБЫЧНОГО ХУДОЖЕСТВЕННОГО ТЕКСТА
                else if (currentActiveTarget != null && !string.IsNullOrWhiteSpace(pText))
                {
                    if (isInListMode)
                    {
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">{Environment.NewLine}";
                        isInListMode = false;
                    }

                    currentActiveTarget.EntryContent += pText;
                }
            }
            // Б) ЧЕСТНЫЙ И НАДЕЖНЫЙ ПАРСЕР ТАБЛИЦ OPENXML (TABLE)
            else if (element is Table table && currentActiveTarget != null)
            {
                if (isInListMode)
                {
                    currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">{Environment.NewLine}";
                    isInListMode = false;
                }

                currentActiveTarget.EntryContent += $"{Environment.NewLine}  <table>";
                foreach (var row in table.Descendants<TableRow>())
                {
                    currentActiveTarget.EntryContent += $"{Environment.NewLine}    <tr>";
                    foreach (var cell in row.Descendants<TableCell>())
                    {
                        string cellText = string.Concat(cell.Descendants<Text>().Select(t => t.Text))
                                                .Replace("\r", "")
                                                .Replace("\n", "")
                                                .Trim();
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}      <td>{cellText}</td>";
                    }
                    currentActiveTarget.EntryContent += $"{Environment.NewLine}    </tr>";
                }
                currentActiveTarget.EntryContent += $"{Environment.NewLine}  </table>";
            }
        }

        if (isInListMode && currentActiveTarget != null)
        {
            currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">";
        }
    }

    // ================================================================= -->
    // OVERRIDE: ХИРУРГИЧЕСКИЙ ZIP-ЭКСТРАКТОР ИЗОБРАЖЕНИЙ ИЗ ФАЙЛОВ WORD  -->
    // ================================================================= -->
    /// <summary>
    /// Извлекает бинарный поток изображения напрямую из ZIP-пакета .docx файла
    /// и нормализует его в Baseline JPEG для 100% совместимости с PDF.
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО CS0115 И CS0534: СИНХРОННЫЙ ZIP-ЭКСТРАКТОР ДЛЯ WORD   -->
    // ================================================================= -->
    /// <summary>
    /// Извлекает бинарный поток изображения напрямую из ZIP-пакета .docx файла
    /// по системной маске поиска без возникновения блокировок потоков (Deadlock).
    /// </summary>
    public override string ExtractImageToCache(string objectId)
    {
        if (string.IsNullOrEmpty(objectId)) return string.Empty;

        // Вычисляем и создаем легитимную иерархическую папку: wordcache\images\w_{hash}\
        string imagesDir = GetImagesDirectory();
        string finalJpgPath = Path.Combine(imagesDir, $"{objectId}.jpg");

        // Если картинка уже была успешно извлечена и перешита в JPEG ранее — мгновенно возвращаем её!
        if (File.Exists(finalJpgPath)) return finalJpgPath;

        // Извлекаем физический путь к исходному файлу .docx из корня дерева
        string? docxPath = this.ChildEntries.FirstOrDefault()?.EntryPath;
        if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath)) return string.Empty;

        try
        {
            // Открываем .docx файл как стандартный ZIP-архив строго в синхронном потоке
            using (var archive = System.IO.Compression.ZipFile.OpenRead(docxPath))
            {
                // В OpenXML внутренние картинки всегда именуются по маске: word/media/image1.png и т.д.
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals($"word/media/{objectId}", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.Equals($"word/media/{objectId}.png", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.Equals($"word/media/{objectId}.jpeg", StringComparison.OrdinalIgnoreCase) ||
                    e.FullName.Equals($"word/media/{objectId}.jpg", StringComparison.OrdinalIgnoreCase));

                if (entry != null)
                {
                    string origExt = Path.GetExtension(entry.FullName);
                    string rawTmpPath = Path.Combine(imagesDir, $"{objectId}_tmp{origExt}");

                    // Физически выгружаем бинарный поток на диск во временный файл
                    entry.ExtractToFile(rawTmpPath, true);

                    if (File.Exists(rawTmpPath))
                    {
                        // Вызываем наш базовый конвертер: копируем и фиксируем в чистый Baseline JPEG!
                        NormalizeAndConvertImage(rawTmpPath, imagesDir, objectId);

                        // Безжалостно зачищаем временный оригинальный файл с диска
                        try { File.Delete(rawTmpPath); } catch { }
                    }
                }
            }

            // Если наш конвертер успешно создал файл — возвращаем абсолютный путь к нему
            if (File.Exists(finalJpgPath))
            {
                return finalJpgPath;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Word На лету Извлечение Сбой] {objectId}: {ex.Message}");
        }

        return string.Empty;
    }


}
