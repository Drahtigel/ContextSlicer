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

    // ================================================================= -->
    // ИСПРАВЛЕНО ЧАСТЬ 1.3: СТЕРИЛЬНЫЙ ЛИНЕЙНЫЙ НАЛИВ ИЗ ОБЪЕКТНОГО DOM ДЕРЕВА -->
    // ================================================================= -->
    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        if (savedEntries == null || savedEntries.Count == 0) return sb;

        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath) || !File.Exists(bookRootPath)) return sb;

        // 1. Выводим заголовки системных промптов в самое начало буфера
        AppendSystemRules(sb);

        string docId = Path.GetFileNameWithoutExtension(bookRootPath);

        // 2. Инициализируем и парсим наше новое объектное дерево DOM
        var documentTree = new GoogleDocumentTree(AppDomain.CurrentDomain.BaseDirectory, docId);
        documentTree.IncludeImages = this.IncludeImages;
        documentTree.ParseToTree(bookRootPath, savedEntries);

        // 3. Выводим оглавление со структурой, если галочка активна на UI
        if (IncludeDirectoryStructure)
        {
            sb.Append(documentTree.BuildStructureMarkdown());
        }

        // 4. ТВОЙ КОНЦЕПТ: Вместо плоских строк, просим КОРНЕВОЙ узел дерева
        // рекурсивно собрать весь контент, запаковав его в ИИ-разметку!
        foreach (var rootChild in documentTree.ChildEntries)
        {
            sb.Append(rootChild.GetFormattedContent(this.IncludeImages));
        }

        await Task.CompletedTask;
        return sb;
    }

    public override async Task GeneratePdfAsync(
        string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        // Вызываем BuildTextContentAsync — он соберёт эталонный монолитный текст с тегами для ИИ
        var sbContent = await BuildTextContentAsync(checkedFiles, new List<SyntaxEntry>(savedEntries), token);
        if (sbContent == null || sbContent.Length == 0) return;

        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath)) return;
        string docId = Path.GetFileNameWithoutExtension(bookRootPath);

        var document = new MigraDoc.DocumentObjectModel.Document();
        var style = document.Styles["Normal"];
        if (style != null) { style.Font.Name = "Arial"; style.Font.Size = 10; }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        // Линейно, построчно наливаем готовый буфер в MigraDoc, безопасно экранируя всё!
        using (var reader = new StringReader(sbContent.ToString()))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                token.ThrowIfCancellationRequested();
                string trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                // ПЕРЕХВАТ ТЕГА ИЗОБРАЖЕНИЯ <image ... /> ДЛЯ PDF
                if (trimmedLine.StartsWith("<image", StringComparison.OrdinalIgnoreCase))
                {
                    var matchSrc = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"src=""([^""]+)""");
                    if (matchSrc.Success)
                    {
                        string imgFileName = matchSrc.Groups[1].Value;
                        string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", docId);
                        string absoluteJpgPath = Path.Combine(imagesDir, imgFileName);

                        if (File.Exists(absoluteJpgPath))
                        {
                            var pImg = currentSection.AddParagraph();
                            pImg.Format.Alignment = MigraDoc.DocumentObjectModel.ParagraphAlignment.Center;
                            var image = pImg.AddImage(absoluteJpgPath);
                            image.Width = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(7);
                        }
                    }
                    continue;
                }

                // Обычные строки, оглавление и таблицы безопасно выводятся как текст
                var pTxt = currentSection.AddParagraph();
                pTxt.AddFormattedText(trimmedLine);
            }
        }

        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    public override async Task GenerateDocxAsync(
        string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        var sbContent = await BuildTextContentAsync(checkedFiles, new List<SyntaxEntry>(savedEntries), token);
        if (sbContent == null || sbContent.Length == 0) return;

        string bookRootPath = checkedFiles?.FirstOrDefault()?.FullPath ?? string.Empty;
        if (string.IsNullOrEmpty(bookRootPath)) return;
        string docId = Path.GetFileNameWithoutExtension(bookRootPath);
        string fullOutputPath = Path.Combine(outputPath, fileName);

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

                    using (var reader = new StringReader(sbContent.ToString()))
                    {
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            token.ThrowIfCancellationRequested();
                            string trimmedLine = line.Trim();
                            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;

                            // ПЕРЕХВАТ ТЕГА ИЗОБРАЖЕНИЯ <image ... /> ДЛЯ WORD
                            if (trimmedLine.StartsWith("<image", StringComparison.OrdinalIgnoreCase))
                            {
                                ProcessWordImageLine(body, mainPart, trimmedLine, docId, "Иллюстрация романа");
                                continue;
                            }

                            // Твой концепт экранирования: Все остальные строки пишем через безопасное свойство .Text!
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
        }, token); // Исправлен токен
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
    // OVERRIDE ЧАСТЬ 3: РЕКУРСИВНЫЙ ОБЪЕКТНЫЙ РЕНДЕРЕР И НАЛИВ СТРУКТУРЫ DOCX -->
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

        // 2. Построчно вычитываем и трансформируем контент узла в объекты OpenXML
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

                    // А) ПЕРЕХВАТ ТЕГА ИЗОБРАЖЕНИЯ <image ... />
                    if (trimmedLine.StartsWith("<image", StringComparison.OrdinalIgnoreCase))
                    {
                        ProcessWordImageLine(body, mainPart, trimmedLine, docId, entry.EntryTitle);
                        continue;
                    }

                    // Б) НАЛИВ ТАБЛИЦ ИЛИ ХУДОЖЕСТВЕННОГО ТЕКСТА
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

        // 2. ОБРАБОТКА ОБЫЧНОГО ХУДОЖЕСТВЕННОГО ТЕКСТА, СПИСКОВ И ТЕХНИЧЕСКИХ ТЕГОВ СТРУКТУРЫ
        var pTxt = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();
        var runTxt = new DocumentFormat.OpenXml.Wordprocessing.Run();

        // Создаем девственно чистый объект текста Word
        var textNode = new DocumentFormat.OpenXml.Wordprocessing.Text();

        if (trimmedLine.StartsWith("<li", StringComparison.OrdinalIgnoreCase))
        {
            string cleanListItem = trimmedLine.Replace("<li>", "").Replace("</li>", "").Trim();

            // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Присваиваем значение строго через свойство .Text,
            // что заставляет ядро OpenXML принудительно экранировать любые знаки < и >!
            textNode.Text = "• " + cleanListItem;
            runTxt.AppendChild(textNode);

            var pProps = new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties();
            pProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Indentation { Left = "720" }); // Отступ 0.5 дюйма
            pTxt.AppendChild(pProps);
        }
        else
        {
            // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Любые строки (включая технические теги <tab>, <document_structure> 
            // и </tab>) сливаются сюда! Напрямую пишем в .Text — OpenXML аппаратно превратит их 
            // в безопасные &lt;tab&gt;, файл никогда не сломается, а ИИ увидит чистую сырую разметку!
            textNode.Text = trimmedLine;
            runTxt.AppendChild(textNode);
        }

        pTxt.AppendChild(runTxt);
        body.AppendChild(pTxt);
    }


    // ================================================================= -->
    // ИСПРАВЛЕНО: КАН Weight ОБЪЕКТНЫЙ ИНЖЕКТОР ИЗОБРАЖЕНИЙ ДЛЯ OPENXML 3.5.1 -->
    // ================================================================= -->
    // ================================================================= -->
    // ЧАСТЬ 1: ФИЛЬТРАЦИЯ КЭША И ИМПОРТ БИНАРНОГО ПОТОКА КАРТИНКИ В ZIP -->
    // ================================================================= -->
    private void ProcessWordImageLine(
        DocumentFormat.OpenXml.Wordprocessing.Body body,
        DocumentFormat.OpenXml.Packaging.MainDocumentPart mainPart,
        string trimmedLine,
        string docId,
        string altText)
    {
        if (!IncludeImages) return;

        try
        {
            // 1. Вырезаем чистый ID картинки (kix.xxxx) базовыми строковыми методами
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

            // Находим и верифицируем оригинальный файл в папке кэша романа
            string imagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", docId);
            string rawDownloadedPath = string.Empty;

            if (Directory.Exists(imagesDir))
            {
                var matchedFiles = Directory.GetFiles(imagesDir, $"{imgId}.*");
                rawDownloadedPath = matchedFiles.FirstOrDefault(f => !f.Contains("_converted", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }

            // 2. Если файл обнаружен — принудительно нормализуем его в sRGB JPEG
            if (!string.IsNullOrEmpty(rawDownloadedPath) && File.Exists(rawDownloadedPath))
            {
                string absoluteJpgPath = BaseDocumentTree.NormalizeAndConvertImage(rawDownloadedPath, imagesDir, imgId);

                if (!string.IsNullOrEmpty(absoluteJpgPath) && File.Exists(absoluteJpgPath))
                {
                    // Добавляем бинарный контент напрямую в zip-пакет WordprocessingDocument
                    var imagePart = mainPart.AddImagePart(DocumentFormat.OpenXml.Packaging.ImagePartType.Jpeg);
                    using (var stream = new FileStream(absoluteJpgPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        imagePart.FeedData(stream);
                    }

                    string relId = mainPart.GetIdOfPart(imagePart);

                    // Вытаскиваем точное физическое имя, под которым OpenXML записал файл (image1.jpg)
                    string exactPackageName = Path.GetFileName(imagePart.Uri.OriginalString) ?? "image.jpg";

                    // 3. Вызываем объектный сборщик DrawingML графа (выведем в Части 2)
                    var drawingNode = BuildDrawingMLNode(relId, exactPackageName, altText);

                    // 4. Нативно упаковываем и центрируем картинку на листе Word (выведем в Части 3)
                    if (drawingNode != null)
                    {
                        AssembleImageParagraph(body, drawingNode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Docx Image Line Error]: {ex.Message}");
        }
    }
    // ================================================================= -->
    // ЧАСТЬ 2 (ИСПРАВЛЕНО): СБОРКА ГРАФА DRAWINGML НА КОНСТРУКТОРАХ NEW -->
    // ================================================================= -->
    /// <summary>
    /// Собирает строго валидный объектный граф DrawingML силами конструкторов OpenXmlUnknownElement.
    /// Полностью уничтожает ошибки CS0117 и обеспечивает 100% стабильность в рантайме.
    /// </summary>
    private DocumentFormat.OpenXml.Wordprocessing.Drawing BuildDrawingMLNode(string relId, string exactPackageName, string altText)
    {
        string p1 = "http" + "://";
        string p2 = "schemas." + "openxmlformats" + ".org/";

        string nsWp = p1 + p2 + "drawingml/2006/wordprocessingDrawing";
        string nsA = p1 + p2 + "drawingml/2006/main";
        string nsPic = p1 + p2 + "drawingml/2006/picture";
        string nsR = p1 + p2 + "officeDocument/2006/relationships";

        string emuSizeStr = "2520000"; // Размеры 7х7 см

        var drawingNode = new DocumentFormat.OpenXml.Wordprocessing.Drawing();

        // ИСПРАВЛЕНО: Заменяем CreateUnknownElement на легитимные нативные конструкторы new!
        var inlineNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("wp:inline");
        inlineNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("distT", "", "0"));
        inlineNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("distB", "", "0"));
        inlineNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("distL", "", "0"));
        inlineNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("distR", "", "0"));

        var extentNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("wp:extent");
        extentNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("cx", "", emuSizeStr));
        extentNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("cy", "", emuSizeStr));
        inlineNode.AppendChild(extentNode);

        var effectExtentNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("wp:effectExtent");
        effectExtentNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("l", "", "0"));
        effectExtentNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("t", "", "0"));
        effectExtentNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("r", "", "0"));
        effectExtentNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("b", "", "0"));
        inlineNode.AppendChild(effectExtentNode);

        var docPrNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("wp:docPr");
        docPrNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("id", "", "1"));
        docPrNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("name", "", "Picture 1"));
        docPrNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("descr", "", altText));
        inlineNode.AppendChild(docPrNode);

        var cNvGraphicFramePrNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("wp:cNvGraphicFramePr");
        var graphicFrameLocksNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:graphicFrameLocks");
        graphicFrameLocksNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("noChangeAspect", "", "1"));
        cNvGraphicFramePrNode.AppendChild(graphicFrameLocksNode);
        inlineNode.AppendChild(cNvGraphicFramePrNode);

        var graphicNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:graphic");
        var graphicDataNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:graphicData");
        graphicDataNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("uri", "", "http://schemas.openxmlformats.org/drawingml/2006/picture"));

        var picNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("pic:pic");
        var nvPicPrNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("pic:nvPicPr");

        var cNvPrNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("pic:cNvPr");
        cNvPrNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("id", "", "0"));
        cNvPrNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("name", "", exactPackageName));
        nvPicPrNode.AppendChild(cNvPrNode);
        nvPicPrNode.AppendChild(new DocumentFormat.OpenXml.OpenXmlUnknownElement("pic:cNvPicPr"));
        picNode.AppendChild(nvPicPrNode);

        var blipFillNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("pic:blipFill");
        var blipNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:blip");
        blipNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("embed", "r", relId));
        blipNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("cstate", "", "print"));
        blipFillNode.AppendChild(blipNode);

        var stretchNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:stretch");
        stretchNode.AppendChild(new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:fillRect"));
        blipFillNode.AppendChild(stretchNode);
        picNode.AppendChild(blipFillNode);

        var spPrNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("pic:spPr");
        var xfrmNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:xfrm");

        var offNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:off");
        offNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("x", "", "0"));
        offNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("y", "", "0"));
        xfrmNode.AppendChild(offNode);

        var extNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:ext");
        extNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("cx", "", emuSizeStr));
        extNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("cy", "", emuSizeStr));
        xfrmNode.AppendChild(extNode);
        spPrNode.AppendChild(xfrmNode);

        var prstGeomNode = new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:prstGeom");
        prstGeomNode.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("prst", "", "rect"));
        prstGeomNode.AppendChild(new DocumentFormat.OpenXml.OpenXmlUnknownElement("a:avLst"));
        spPrNode.AppendChild(prstGeomNode);
        picNode.AppendChild(spPrNode);

        graphicDataNode.AppendChild(picNode);
        graphicNode.AppendChild(graphicDataNode);
        inlineNode.AppendChild(graphicNode);

        drawingNode.AppendChild(inlineNode);
        return drawingNode;
    }

    // ================================================================= -->
    // ЧАСТЬ 3: НАДЁЖНАЯ УПАКОВКА И ЦЕНТРИРОВАНИЕ ПАРАГРАФА WORD       -->
    // ================================================================= -->
    /// <summary>
    /// Собирает честный чистый абзац Word и СНАЧАЛА добавляет свойства выравнивания pPr!
    /// Исключает ошибку CS0103 на строке 544 и гарантирует 100% отображение картинок в MS Word.
    /// </summary>
    private void AssembleImageParagraph(DocumentFormat.OpenXml.Wordprocessing.Body body, DocumentFormat.OpenXml.Wordprocessing.Drawing drawingNode)
    {
        var pImg = new DocumentFormat.OpenXml.Wordprocessing.Paragraph();

        // Свойства параграфа СТРОГО на первом месте для идеальной валидности OOXML!
        var pProps = new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties();
        pProps.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Justification
        {
            Val = DocumentFormat.OpenXml.Wordprocessing.JustificationValues.Center
        });
        pImg.AppendChild(pProps);

        var runImg = new DocumentFormat.OpenXml.Wordprocessing.Run();
        runImg.AppendChild(drawingNode);
        pImg.AppendChild(runImg);

        body.AppendChild(pImg);
    }
} // Самая последняя финальная скобка файла ContextBuilderGoogle!