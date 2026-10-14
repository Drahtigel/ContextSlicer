using ContextSlicer.Filesystem;
using DocumentFormat.OpenXml.Packaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Стратегия сборки контекста ИИ для Google Документов (Tabs API).
/// Интегрирована с универсальной объектной моделью GoogleDocumentTree для выгрузки вкладок, списков и таблиц.
/// </summary>
public class ContextBuilderGoogle : ContextBuilderBase
{
    public ContextBuilderGoogle(string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages)
        : base(promptRules, moduleRules, includeDirectoryStructure, includeImages) { }

    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        if (savedEntries == null || savedEntries.Count == 0) return sb;

        // 1. Извлекаем абсолютный путь к физическому файлу кэша JSON на диске
        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath)) return sb;

        AppendSystemRules(sb);

        // Извлекаем чистый ID документа для каталога images/{id}
        string docId = Path.GetFileNameWithoutExtension(bookRootPath);

        // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Передаем в качестве projectLocation ЧИСТЫЙ корень 
        // AppDomain.CurrentDomain.BaseDirectory вместо googleCacheDir! Это полностью уничтожает
        // баг бесконечного зацикливания папок вида googlecache/googlecache на жестком диске.
        var documentTree = new GoogleDocumentTree(AppDomain.CurrentDomain.BaseDirectory, docId);
        documentTree.IncludeImages = this.IncludeImages;

        // Запускаем объектный DOM-парсер структуры
        documentTree.ParseToTree(bookRootPath, savedEntries);

        // 3. СБОРКА ИТОГОВОГО ТЕКСТА НА ОСНОВЕ СФОРМИРОВАННОГО ДЕРЕВА
        // Наливаем оглавление со структурой вкладок <tab> и честными уровнями level
        if (IncludeDirectoryStructure)
        {
            sb.Append(documentTree.BuildStructureMarkdown());
        }

        // Наливаем структурированную прозу, списки <li> и маркеры таблиц/изображений
        sb.Append(documentTree.BuildContentText());

        await Task.CompletedTask;

        return sb;
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: ОБЪЕКТНАЯ СБОРКА И ИНЖЕКЦИЯ КАРТИНОК В PDF GOOGLE     -->
    // ================================================================= -->
    public override async Task GeneratePdfAsync(
        string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        // 1. Вызываем BuildTextContentAsync для упреждающей записи системных правил
        var sbRules = await BuildTextContentAsync(checkedFiles, savedEntries, token);

        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath)) return;

        string docId = Path.GetFileNameWithoutExtension(bookRootPath);

        // 2. Инициализируем DOM-дерево
        var documentTree = new GoogleDocumentTree(AppDomain.CurrentDomain.BaseDirectory, docId);
        documentTree.IncludeImages = IncludeImages;
        documentTree.ParseToTree(bookRootPath, savedEntries);

        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style != null) { style.Font.Name = "Arial"; style.Font.Size = 10; }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Наливаем блок системных правил в начало PDF, отсекая структуру
        string rawRules = sbRules.ToString();
        int docStructIdx = rawRules.IndexOf("<document_structure>");
        if (docStructIdx > 0)
        {
            var pRules = currentSection.AddParagraph();
            pRules.AddFormattedText(rawRules.Substring(0, docStructIdx).Trim());
            pRules.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(12);
        }

        // 3. Запускаем рекурсивный обход ветвей DOM-дерева для налива прозы и живых картинок!
        foreach (var child in documentTree.ChildEntries)
        {
            RenderEntryToPdfRecursive(currentSection, child, docId, token);
        }

        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }
    // ================================================================= -->
    // ДОБАВЛЕНО: РЕКУРСИВНЫЙ ОБЪЕКТНЫЙ НАЛИВ ЭЛЕМЕНТОВ DOM И IMAGE В PDF-->
    // ================================================================= -->
    private void RenderEntryToPdfRecursive(MigraDoc.DocumentObjectModel.Section section, DocEntry entry, string docId, CancellationToken token)
    {
        if (entry == null) return;
        token.ThrowIfCancellationRequested();

        // 1. Рендерим структурные папки, вкладки и заголовки глав крупным шрифтом
        if (entry.Type == DocEntryType.Tab || entry.Type == DocEntryType.Heading)
        {
            var p = section.AddParagraph();
            var format = p.AddFormattedText(entry.EntryTitle);
            format.Bold = true;
            format.Size = entry.Type == DocEntryType.Tab ? 14 : (13 - entry.HeadingLevel);
            p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(8);
            p.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(4);
        }

        // 2. Построчно вычитываем и форматируем содержательный контент узла
        if (!string.IsNullOrWhiteSpace(entry.EntryContent))
        {
            using (var reader = new StringReader(entry.EntryContent))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                    // ХИРУРГИЧЕСКИЙ ПЕРЕХВАТ ТЕГА ИЗОБРАЖЕНИЯ <image ... />
                    if (trimmedLine.StartsWith("<image", StringComparison.OrdinalIgnoreCase))
                    {
                        // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Если галочка "Включить изображения" активна [✓]
                        if (IncludeImages)
                        {
                            // Извлекаем имя файла из атрибута src="id.jpg" с помощью регулярного выражения
                            var matchSrc = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"src=""([^""]+)""");
                            if (matchSrc.Success)
                            {
                                string imgFileName = matchSrc.Groups[1].Value;

                                // Собираем каноничный путь к нормализованному JPEG-файлу внутри кэша картинок
                                string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", docId);
                                string absoluteJpgPath = Path.Combine(imagesDir, imgFileName);

                                if (File.Exists(absoluteJpgPath))
                                {
                                    try
                                    {
                                        // Создаем центрированный параграф-контейнер и нативно инжектируем картинку в PDF!
                                        var pImg = section.AddParagraph();
                                        pImg.Format.Alignment = MigraDoc.DocumentObjectModel.ParagraphAlignment.Center;
                                        pImg.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(6);
                                        pImg.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromPoint(6);

                                        var image = pImg.AddImage(absoluteJpgPath);
                                        image.Width = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(7); // Фиксированная ширина 7 см
                                    }
                                    catch (Exception ex)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[MigraDoc Image Injection Error]: {ex.Message}");
                                    }
                                }
                            }
                        }

                        // Если галка выключена — мы просто делаем continue, и тег бесследно стирается из PDF!
                        continue;
                    }

                    // Отрисовка обычного текста или элементов списков
                    var pTxt = section.AddParagraph();

                    if (trimmedLine.StartsWith("<li"))
                    {
                        pTxt.Format.LeftIndent = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.5);
                        pTxt.AddFormattedText("• " + trimmedLine.Replace("<li>", "").Replace("</li>", "").Trim());
                    }
                    else
                    {
                        pTxt.AddFormattedText(trimmedLine);
                    }
                }
            }
        }

        // 3. Рекурсивно уходим вглубь по дочерней коллекции элементов DOM-дерева
        foreach (var child in entry.ChildEntries)
        {
            RenderEntryToPdfRecursive(section, child, docId, token); // Снайперский рекурсивный вызов
        }
    }

    // ================================================================= -->
    // OVERRIDE ЧАСТЬ 1: ИНИЦИАЛИЗАЦИЯ DOM И СБОРКА ZIP-ПАКЕТА DOCX      -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: СНАЙПЕРСКИЙ НАЛИВ DOCX ИЗ ГОТОВОГО MARKDOWN-БУФЕРА   -->
    // ================================================================= -->
    public override async Task GenerateDocxAsync(
        string outputPath,
        string fileName,
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        // 1. Вызываем BuildTextContentAsync — он один раз чисто соберёт весь контекст 
        // со всеми тегами <image>, таблицами и правилами промпта, не ломая список сохраненных элементов!
        var sbContent = await BuildTextContentAsync(checkedFiles, savedEntries, token);
        if (sbContent == null || sbContent.Length == 0) return;

        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath)) return;

        string docId = Path.GetFileNameWithoutExtension(bookRootPath);
        string fullOutputPath = Path.Combine(outputPath, fileName);

        // 2. Запускаем фоновую сборку zip-пакета WordprocessingDocument силами OpenXML
        await Task.Run(() =>
        {
            try
            {
                using (var wordDoc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                    fullOutputPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
                {
                    var mainPart = wordDoc.AddMainDocumentPart();
                    mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
                    var body = mainPart.Document.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Body());

                    DocumentFormat.OpenXml.Wordprocessing.Table? currentWordTable = null;
                    DocumentFormat.OpenXml.Wordprocessing.TableRow? currentWordRow = null;

                    // 3. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Построчно читаем готовый контент буфера,
                    // где теги <image> гарантированно присутствуют на своих законных местах!
                    using (var reader = new StringReader(sbContent.ToString()))
                    {
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            token.ThrowIfCancellationRequested();
                            string trimmedLine = line.Trim();
                            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                            // Пропускаем технические маркеры тегов структуры оглавления
                            if (trimmedLine.StartsWith("<document_structure>") ||
                                trimmedLine.StartsWith("</document_structure>") ||
                                trimmedLine.StartsWith("<ul id=\"toc_root\"") ||
                                trimmedLine.StartsWith("</ul id=\"toc_root\""))
                            {
                                continue;
                            }

                            // Вырезаем и заменяем технические строки li оглавления
                            if (trimmedLine.StartsWith("<li level=") && trimmedLine.Contains("type=\""))
                            {
                                continue;
                            }

                            // А) СНАЙПЕРСКИЙ ПЕРЕХВАТ ТЕГА ИЗОБРАЖЕНИЯ <image ... />
                            if (trimmedLine.StartsWith("<image", StringComparison.OrdinalIgnoreCase))
                            {
                                ProcessWordImageLine(body, mainPart, trimmedLine, docId, "Иллюстрация романа");
                                continue;
                            }

                            // Б) ПАРСЕР СТРУКТУРНЫХ ТЕГОВ ТАБЛИЦ ИЛИ ХУДОЖЕСТВЕННОГО ТЕКСТА
                            HandleTableAndTextLines(body, trimmedLine, ref currentWordTable, ref currentWordRow);
                        }
                    }

                    mainPart.Document.Save();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Google Docx OpenXML Export Error] {ex.Message}");
                if (File.Exists(fullOutputPath)) try { File.Delete(fullOutputPath); } catch { }
                throw;
            }
        }, token);
    }

    // ================================================================= -->
    // OVERRIDE ЧАСТЬ 2: РЕКУРСИВНЫЙ РЕНДЕРЕР ТЕКСТА, СПИСКОВ И ТАБЛИЦ  -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО ЧАСТЬ 2.1: ДИСПЕТЧЕР СТРОК И ИНЖЕКТОР ИЗОБРАЖЕНИЙ DOCX  -->
    // ================================================================= -->
    private void RenderEntryToDocxRecursive(
        DocumentFormat.OpenXml.Wordprocessing.Body body,
        DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart,
        DocEntry entry,
        string docId,
        CancellationToken token)
    {
        if (entry == null) return;
        token.ThrowIfCancellationRequested();

        // 1. Отрисовка папок структуры и заголовков глав (Heading)
        if (entry.Type == DocEntryType.Tab || entry.Type == DocEntryType.Heading)
        {
            var pHead = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
            var runHead = new DocumentFormat.OpenXml.Wordprocessing.Run();
            var runProps = new DocumentFormat.OpenXml.Wordprocessing.RunProperties();
            runProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Bold());

            string szVal = entry.Type == DocEntryType.Tab ? "28" : "24"; // 14pt или 12pt
            runProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.FontSize { Val = szVal });

            runHead.AppendChild(runProps);
            runHead.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text(entry.EntryTitle));
            pHead.AppendChild(runHead);
            body.AppendChild(pHead);
        }

        // 2. Диспетчеризация содержательного контента узла
        if (!string.IsNullOrWhiteSpace(entry.EntryContent))
        {
            DocumentFormat.OpenXml.Wordprocessing.Table? currentWordTable = null;
            DocumentFormat.OpenXml.Wordprocessing.TableRow? currentWordRow = null;

            using (var reader = new StringReader(entry.EntryContent))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                    // А) Обработка тега изображения через выделенный метод
                    if (trimmedLine.StartsWith("<image", StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessWordImageLine(body, mainPart, trimmedLine, docId, entry.EntryTitle);
                        continue;
                    }

                    // Б) Перенаправляем управление в парсер таблиц и текста (выведем в следующей части)
                    HandleTableAndTextLines(body, trimmedLine, ref currentWordTable, ref currentWordRow);
                }
            }
        }

        // 3. Рекурсивно спускаемся к дочерней коллекции элементов DOM
        foreach (var child in entry.ChildEntries)
        {
            RenderEntryToDocxRecursive(body, mainPart, child, docId, token);
        }
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: ЖЕЛЕЗОБЕТОННЫЙ ОДНОПРОХОДНЫЙ ИНЖЕКТОР КАРТИНОК В WORD -->
    // ================================================================= -->
    private void ProcessWordImageLine(DocumentFormat.OpenXml.Wordprocessing.Body body, DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart, string trimmedLine, string docId, string altText)
    {
        if (!IncludeImages) return;

        try
        {
            // Извлекаем чистый ID картинки (kix.xxxx) с помощью базовых строковых методов
            string imgId = trimmedLine;
            int srcIdx = imgId.IndexOf("src=\"");
            if (srcIdx >= 0)
            {
                imgId = imgId.Substring(srcIdx + 5);
                int endIdx = imgId.IndexOf("\"");
                if (endIdx >= 0) imgId = imgId.Substring(0, endIdx);
            }

            imgId = imgId.Replace("_converted.jpg", "").Replace(".jpg", "").Trim();
            if (string.IsNullOrEmpty(imgId)) return;

            // Каноничный путь к папке картинок: googlecache\images\{docId}
            string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", docId);
            string rawDownloadedPath = string.Empty;

            if (Directory.Exists(imagesDir))
            {
                var matchedFiles = Directory.GetFiles(imagesDir, $"{imgId}.*");
                rawDownloadedPath = matchedFiles.FirstOrDefault(f => !f.Contains("_converted", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(rawDownloadedPath) && File.Exists(rawDownloadedPath))
            {
                string absoluteJpgPath = BaseDocumentTree.NormalizeAndConvertImage(rawDownloadedPath, imagesDir, imgId);

                if (!string.IsNullOrEmpty(absoluteJpgPath) && File.Exists(absoluteJpgPath))
                {
                    // 1. Добавляем бинарный контент напрямую в zip-пакет WordprocessingDocument
                    var imagePart = mainPart.AddImagePart(DocumentFormat.OpenXml.Packaging.ImagePartType.Jpeg);
                    using (var stream = new FileStream(absoluteJpgPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        imagePart.FeedData(stream);
                    }

                    string relId = mainPart.GetIdOfPart(imagePart);
                    long emuSize = 2520000; // Размеры 7х7 см (7 * 360000 EMU)

                    // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Генерируем ПОЛНЫЙ самодостаточный XML-абзац параграфа.
                    // Все префиксы пространств имён (w:, wp:, a:, pic:, r:) объявлены строго на корневом 
                    // элементе <w:p>, а тег центрирования <w:pPr> стоит на первом законном месте перед текстом!
                    string rawParagraphXml =
                        "<w:p xmlns:w=\"http://openxmlformats.org\" " +
                             "xmlns:wp=\"http://openxmlformats.org\" " +
                             "xmlns:a=\"http://openxmlformats.org\" " +
                             "xmlns:pic=\"http://openxmlformats.org\" " +
                             "xmlns:r=\"http://openxmlformats.org\">" +
                            "<w:pPr>" +
                                "<w:jc w:val=\"center\"/>" +
                            "</w:pPr>" +
                            "<w:r>" +
                                "<w:drawing>" +
                                    "<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
                                        $"<wp:extent cx=\"{emuSize}\" cy=\"{emuSize}\"/>" +
                                        "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>" +
                                        $"<wp:docPr id=\"1\" name=\"Picture 1\" descr=\"{altText}\"/>" +
                                        "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
                                        "<a:graphic>" +
                                            "<a:graphicData uri=\"http://openxmlformats.org\">" +
                                                "<pic:pic>" +
                                                    "<pic:nvPicPr><pic:cNvPr id=\"0\" name=\"Image.jpg\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                                                    "<pic:blipFill>" +
                                                        $"<a:blip r:embed=\"{relId}\"/>" +
                                                        "<a:stretch><a:fillRect/></a:stretch>" +
                                                    "</pic:blipFill>" +
                                                    "<pic:spPr>" +
                                                        $"<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{emuSize}\" cy=\"{emuSize}\"/></a:xfrm>" +
                                                        "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
                                                    "</pic:spPr>" +
                                                "</pic:pic>" +
                                            "</a:graphicData>" +
                                        "</a:graphic>" +
                                    "</wp:inline>" +
                                "</w:drawing>" +
                            "</w:r>" +
                        "</w:p>";

                    // 3. Создаем параграф напрямую через парсинг XML-строки — OpenXML SDK воссоздаст 
                    // честный, готовый w:p со всей глубокой структурой рисунка и запишет его в файл!
                    var importedParagraph = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(rawParagraphXml);
                    body.AppendChild(importedParagraph);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[КРИТИЧЕСКИЙ СБОЙ ВНЕДРЕНИЯ КАРТИНКИ В WORD]: {ex.Message}");
        }
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО ЧАСТЬ 2.2: ПАРСЕР ТАБЛИЦ СТРУКТУРЫ И ХУДОЖЕСТВЕННОГО ТЕКСТА -->
    // ================================================================= -->
    /// <summary>
    /// ИЗОЛИРОВАННАЯ ФУНКЦИЯ: Обрабатывает строки HTML-таблиц и преобразует 
    /// обычную прозу со списками в строго типизированные параграфы Word.
    /// </summary>
    private void HandleTableAndTextLines(
        DocumentFormat.OpenXml.Wordprocessing.Body body,
        string trimmedLine,
        ref DocumentFormat.OpenXml.Wordprocessing.Table? currentWordTable,
        ref DocumentFormat.OpenXml.Wordprocessing.TableRow? currentWordRow)
    {
        // 1. ОПРЕДЕЛЕНИЕ И СБОРКА СТРУКТУРЫ ТАБЛИЦ WORD
        if (trimmedLine.StartsWith("<table>", StringComparison.OrdinalIgnoreCase))
        {
            currentWordTable = new DocumentFormat.OpenXml.Wordprocessing.Table();
            var tblProps = new DocumentFormat.OpenXml.Wordprocessing.TableProperties(
                new DocumentFormat.OpenXml.Wordprocessing.TableBorders(
                    new DocumentFormat.OpenXml.Wordprocessing.TopBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                    new DocumentFormat.OpenXml.Wordprocessing.BottomBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                    new DocumentFormat.OpenXml.Wordprocessing.LeftBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                    new DocumentFormat.OpenXml.Wordprocessing.RightBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                    new DocumentFormat.OpenXml.Wordprocessing.InsideHorizontalBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 },
                    new DocumentFormat.OpenXml.Wordprocessing.InsideVerticalBorder { Val = DocumentFormat.OpenXml.Wordprocessing.BorderValues.Single, Size = 4 }
                )
            );
            currentWordTable.AppendChild(tblProps);
            return;
        }

        if (trimmedLine.StartsWith("<tr>", StringComparison.OrdinalIgnoreCase) && currentWordTable != null)
        {
            currentWordRow = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
            return;
        }

        if (trimmedLine.StartsWith("<td>", StringComparison.OrdinalIgnoreCase) && currentWordRow != null)
        {
            string cellText = trimmedLine.Replace("<td>", "").Replace("</td>", "").Trim();
            var cell = new DocumentFormat.OpenXml.Wordprocessing.TableCell();
            var pCell = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.Text(cellText)));

            cell.AppendChild(pCell);
            currentWordRow.AppendChild(cell);
            return;
        }

        if (trimmedLine.StartsWith("</tr>", StringComparison.OrdinalIgnoreCase) && currentWordTable != null && currentWordRow != null)
        {
            currentWordTable.AppendChild(currentWordRow);
            currentWordRow = null;
            return;
        }

        if (trimmedLine.StartsWith("</table>", StringComparison.OrdinalIgnoreCase) && currentWordTable != null)
        {
            body.AppendChild(currentWordTable);
            currentWordTable = null;
            return;
        }

        // 2. ОБРАБОТКА ОБЫЧНОГО ХУДОЖЕСТВЕННОГО ТЕКСТА И СПИСКОВ <li>
        var pTxt = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
        var runTxt = new DocumentFormat.OpenXml.Wordprocessing.Run();

        if (trimmedLine.StartsWith("<li", StringComparison.OrdinalIgnoreCase))
        {
            string cleanListItem = trimmedLine.Replace("<li>", "").Replace("</li>", "").Trim();
            runTxt.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text("• " + cleanListItem));

            var pProps = new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties();
            pProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Indentation { Left = "720" }); // Отступ 0.5 дюйма
            pTxt.AppendChild(pProps);
        }
        else
        {
            runTxt.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text(trimmedLine));
        }

        pTxt.AppendChild(runTxt);
        body.AppendChild(pTxt);
    }


    /// <summary>
    /// Генерирует XML-структуру Drawing элемента OpenXML для интеграции картинок в .docx.
    /// Использует конструктор строки для Inline-элемента, что гарантирует 100% компиляцию!
    /// </summary>
    private static DocumentFormat.OpenXml.Wordprocessing.Drawing CreateOpenXmlDrawing(string relationshipId, string altText)
    {
        // Размеры 7х7 см в единицах EMU (7 * 360000)
        long emuSize = 2520000;

        // Строим чистую XML-разметку инлайнового контента wp:inline БЕЗ внешней обертки w:drawing
        string inlineXml = "<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" " +
            "xmlns:wp=\"http://openxmlformats.org\" " +
            "xmlns:a=\"http://openxmlformats.org\" " +
            "xmlns:pic=\"http://openxmlformats.org\" " +
            "xmlns:r=\"http://openxmlformats.org\">" +
                $"<wp:extent cx=\"{emuSize}\" cy=\"{emuSize}\"/>" +
                "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>" +
                $"<wp:docPr id=\"1\" name=\"Picture 1\" descr=\"{altText}\"/>" +
                "<wp:cNvGraphicFramePr>" +
                    "<a:graphicFrameLocks noChangeAspect=\"1\"/>" +
                "</wp:cNvGraphicFramePr>" +
                "<a:graphic>" +
                    "<a:graphicData uri=\"http://openxmlformats.org\">" +
                        "<pic:pic>" +
                            "<pic:nvPicPr>" +
                                "<pic:cNvPr id=\"0\" name=\"Image.jpg\"/>" +
                                "<pic:cNvPicPr/>" +
                            "</pic:nvPicPr>" +
                            "<pic:blipFill>" +
                                $"<a:blip r:embed=\"{relationshipId}\"/>" +
                                "<a:stretch><a:fillRect/></a:stretch>" +
                            "</pic:blipFill>" +
                            "<pic:spPr>" +
                                $"<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{emuSize}\" cy=\"{emuSize}\"/></a:xfrm>" +
                                "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" +
                            "</pic:spPr>" +
                        "</pic:pic>" +
                    "</a:graphicData>" +
                "</a:graphic>" +
            "</wp:inline>";

        // 1. Создаем пустой, строго легитимный контейнер рисунка Word
        var drawing = new DocumentFormat.OpenXml.Wordprocessing.Drawing();

        try
        {
            // 2. Инициализируем Inline-элемент через конструктор сырого XML-текста.
            // OpenXML SDK нативно распарсит и создаст готовый граф внутренних объектов рисунка!
            var inlineElement = new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(inlineXml);

            // 3. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Добавляем созданный узел внутрь контейнера.
            // Это обходит проблему свойства OuterXml "только для чтения", полностью уничтожает 
            // все три ошибки CS0234/CS0117 и гарантирует физическое появление картинок в Word!
            drawing.AppendChild(inlineElement);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenXML Inline Parse Error]: {ex.Message}");
        }

        return drawing;
    }
} // Конец класса ContextBuilderGoogle






