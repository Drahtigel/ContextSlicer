using ContextSlicer.Filesystem;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Реализация объектного дерева документа для формата Google Docs (Tabs API).
/// Выполняет специализированный разбор многовкладочного JSON в универсальные узлы DocEntry.
/// </summary>
public class GoogleDocumentTree : BaseDocumentTree
{
    public GoogleDocumentTree(string cacheDirectory, string documentId) : base(cacheDirectory, documentId) { }
    
    /// <summary>
    /// Главная точка входа: вычитывает JSON кэша и запускает рекурсивную сборку дерева.
    /// </summary>
    public override void ParseToTree(string sourcePath, List<SyntaxEntry> savedEntries)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || savedEntries == null || savedEntries.Count == 0)
            return;

        try
        {
            string jsonContent = File.ReadAllText(sourcePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(jsonContent)) return;

            var docJson = JObject.Parse(jsonContent);
            this.DocTitle = docJson["title"]?.ToString() ?? "Без названия";

            // Сортируем входной список сохраненных элементов для линейного прохода
            var checkList = savedEntries.ToList();

            // ПРОВЕРКА СТРУКТУРЫ: Если это новый формат с вкладками "tabs"
            if (docJson["tabs"] is JArray tabsArray)
            {
                foreach (var tabToken in tabsArray)
                {
                    ParseTabRecursive(tabToken, checkList, string.Empty, null);
                }
            }
            // СТАРЫЙ ФОЛБЭК: Если документ сохранен в классическом плоском формате без вкладок
            else if (docJson["body"]?["content"] is JArray contentArray)
            {
                var fakeTabNode = new DocEntry
                {
                    EntryTitle = this.DocTitle,
                    EntryPath = this.DocTitle,
                    Type = DocEntryType.Tab
                };
                this.ChildEntries.Add(fakeTabNode);

                ParseParagraphsToTree(contentArray, fakeTabNode, checkList, this.DocTitle);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Google DOM Parse Error] {ex.Message}");
        }
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: ЧЕСТНОЕ ИЕРАРХИЧЕСКОЕ ВЫСТРАИВАНИЕ ВКЛАДОК TABS API   -->
    // ================================================================= -->
    private void ParseTabRecursive(JToken tab, List<SyntaxEntry> savedEntries, string currentPath, DocEntry? parentEntry)
    {
        if (tab == null) return;

        string defTabTitle = "Вкладка";
        string tabTitle = tab["tabProperties"]?["title"]?.ToString() ?? defTabTitle;
        string fullPath = string.IsNullOrEmpty(currentPath) ? tabTitle : $"{currentPath}/{tabTitle}";

        // Проверяем одобрение вкладки или её потомков на UI
        bool isTabApproved = savedEntries.Any(e =>
            e != null && !string.IsNullOrEmpty(e.EntryPath) &&
            (e.EntryPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) ||
             e.EntryPath.StartsWith(fullPath + "/", StringComparison.OrdinalIgnoreCase)));

        if (isTabApproved)
        {
            var tabEntry = new DocEntry
            {
                EntryTitle = tabTitle,
                EntryPath = fullPath,
                Type = DocEntryType.Tab,
                HeadingLevel = 0,
                Parent = parentEntry // Привязываем физического родителя узла
            };

            // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Если родитель существует — кладём подвкладку строго 
            // внутрь его коллекции, а не в корень документа! Это восстанавливает честную вложенность!
            if (parentEntry != null)
            {
                parentEntry.ChildEntries.Add(tabEntry);
            }
            else
            {
                this.ChildEntries.Add(tabEntry);
            }

            var directMatch = savedEntries.FirstOrDefault(e => e.Type == EntryType.Tab && e.EntryPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (directMatch != null) savedEntries.Remove(directMatch);

            // Разбираем контент (параграфы, списки, таблицы) текущей вкладки
            var content = tab["documentTab"]?["body"]?["content"] as JArray;
            if (content != null)
            {
                ParseParagraphsToTree(content, tabEntry, savedEntries, fullPath);
            }

            // Рекурсивно уходим вглубь по дочерним подвкладкам, передавая текущую вкладку как родителя!
            if (tab["childTabs"] is JArray childTabs)
            {
                foreach (var childTab in childTabs)
                {
                    ParseTabRecursive(childTab, savedEntries, fullPath, tabEntry); // <-- ПЕРЕДАЕМ РОДИТЕЛЯ
                }
            }
        }
    }

    /// <summary>
    /// Главный диспетчер обхода элементов: линейно перенаправляет параграфы и таблицы в профильные методы.
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: СТЕРИЛЬНЫЙ КОНВЕЙЕР РАЗБОРА СТРУКТУРЫ И ПРОЗЫ GOOGLE  -->
    // ================================================================= -->
    private void ParseParagraphsToTree(JArray contentArray, DocEntry parentTab, List<SyntaxEntry> savedEntries, string tabPath)
    {
        var activeChain = new List<DocEntry> { parentTab };
        var activePaths = new Dictionary<int, string> { { 0, tabPath } };
        DocEntry? currentActiveTarget = parentTab;

        bool isInListMode = false;
        string currentListId = "l_generic";

        for (int i = 0; i < contentArray.Count; i++)
        {
            var element = contentArray[i] as JObject;
            if (element == null) continue;

            if (element["paragraph"] is JObject paragraph)
            {
                // ИСПРАВЛЕНО: Полностью удален вызов дублирующего метода ProcessCodeInlineImages!
                // Текст и картинки теперь обрабатываются СТРОГО в один проход внутри ProcessCodeParagraph.
                currentActiveTarget = ProcessCodeParagraph(paragraph, savedEntries, tabPath,
                    activeChain, activePaths, currentActiveTarget, ref isInListMode, ref currentListId);
            }
            else if (element["table"] != null && currentActiveTarget != null)
            {
                ProcessCodeTable(element, currentActiveTarget, ref isInListMode, currentListId);
            }
        }
    }

    private DocEntry? ProcessCodeParagraph(JObject paragraph, List<SyntaxEntry> savedEntries, string tabPath,
        List<DocEntry> activeChain, Dictionary<int, string> activePaths, DocEntry? currentActiveTarget,
        ref bool isInListMode, ref string currentListId)
    {
        string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;
        var textBuilder = new StringBuilder();

        var elements = paragraph["elements"] as JArray;
        if (elements != null)
        {
            int imageIndex = -1;
            foreach (var el in elements)
            {
                if (el == null) continue;

                if (el["textRun"]?["content"] is JToken textToken)
                {
                    textBuilder.Append(textToken.ToString());
                }
                else if (IncludeImages && el["inlineObjectElement"]?["inlineObjectId"] is JToken imgToken)
                {
                    string imgId = imgToken.ToString();
                    if (!string.IsNullOrEmpty(imgId))
                    {
                        imageIndex++;
                        string altName = currentActiveTarget != null ? currentActiveTarget.EntryTitle : "Изображение";
                        if (imageIndex > 0) altName = $"{altName} [{imageIndex}]";

                        string imagesDir = GetImagesDirectory();
                        string rawDownloadedPath = string.Empty;

                        if (Directory.Exists(imagesDir))
                        {
                            // ================================================================= -->
                            // ИСПРАВЛЕНО: ТОЧНЫЙ ОТСЕВ МАРКЕРНЫХ КОПИЙ БЕЗ БЛОКИРОВКИ ИСХОДНЫХ JPG-->
                            // ================================================================= -->
                            // Нативно и мгновенно ищем скачанный файл по системной маске objectId.*
                            var matchedFiles = Directory.GetFiles(imagesDir, $"{imgId}.*");

                            // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Отсекаем СТРОГО те файлы, которые уже содержат 
                            // маркер "_converted". Это позволит исходным файлам .jpg Беатрис беспрепятственно 
                            // проходить на глубокую конверсию и sRGB реанимацию!
                            rawDownloadedPath = matchedFiles.FirstOrDefault(f => !f.Contains("_converted", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

                        }

                        if (!string.IsNullOrEmpty(rawDownloadedPath) && File.Exists(rawDownloadedPath))
                        {
                            string convertedJpgPath = ExtractImageToCache(imgId);
                            if (!string.IsNullOrEmpty(convertedJpgPath))
                            {
                                // Вшиваем в тег имя файла с суффиксом конверсии
                                textBuilder.Append($"{Environment.NewLine}<image src=\"{imgId}_converted.jpg\" alt=\"{altName}\" />{Environment.NewLine}");
                            }
                        }

                    }
                }
            }
        }

        string pText = textBuilder.ToString();
        string headingTitle = pText.Trim();

        // Очищаем cleanHeadingName от тегов картинок для точного текстового маппинга папок структуры
        string cleanHeadingName = headingTitle;
        if (cleanHeadingName.Contains("<image"))
        {
            int imgIdx = cleanHeadingName.IndexOf("<image");
            cleanHeadingName = cleanHeadingName.Substring(0, imgIdx).Trim();
        }
        cleanHeadingName = cleanHeadingName.TrimEnd('.');

        int headerLevel = 1;
        bool isHeadingStyle = namedStyle.StartsWith("HEADING_") && int.TryParse(namedStyle.Substring(8), out headerLevel);
        bool isTitleOrSubtitle = namedStyle.Equals("TITLE", StringComparison.OrdinalIgnoreCase) || namedStyle.Equals("SUBTITLE", StringComparison.OrdinalIgnoreCase);
        bool isVirtualFolderMatch = !string.IsNullOrEmpty(cleanHeadingName) &&
                                    savedEntries.Any(e => e.EntryPath.EndsWith(cleanHeadingName, StringComparison.OrdinalIgnoreCase));

        // А) ВЕТКА СТРУКТУРНЫХ ЗАГОЛОВКОВ И ПАПОК
        if (isHeadingStyle || isTitleOrSubtitle || isVirtualFolderMatch)
        {
            if (isInListMode && currentActiveTarget != null)
            {
                currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">";
                isInListMode = false;
            }

            if (string.IsNullOrEmpty(headingTitle)) return currentActiveTarget;
            int targetLevel = isHeadingStyle ? headerLevel : (currentActiveTarget != null ? currentActiveTarget.HeadingLevel + 1 : 1);

            while (activeChain.Count > 1 && activeChain[activeChain.Count - 1].HeadingLevel >= targetLevel)
                activeChain.RemoveAt(activeChain.Count - 1);

            DocEntry correctParent = activeChain[activeChain.Count - 1];
            int targetParentLevel = targetLevel - 1;
            while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel)) targetParentLevel--;

            string parentPath = activePaths[targetParentLevel];
            string fullHeadingPath = string.IsNullOrEmpty(parentPath) ? cleanHeadingName : $"{parentPath}/{cleanHeadingName}";

            var match = savedEntries.FirstOrDefault(e => e.EntryPath.Trim().TrimEnd('.').Equals(fullHeadingPath, StringComparison.OrdinalIgnoreCase))
                        ?? savedEntries.FirstOrDefault(e => e.EntryPath.EndsWith("/" + cleanHeadingName, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                DocEntryType localType = namedStyle.Equals("TITLE", StringComparison.OrdinalIgnoreCase) ? DocEntryType.Title :
                                         namedStyle.Equals("SUBTITLE", StringComparison.OrdinalIgnoreCase) ? DocEntryType.Subtitle : DocEntryType.Heading;

                var headingEntry = new DocEntry { EntryTitle = cleanHeadingName, EntryPath = match.EntryPath, Type = localType, HeadingLevel = targetLevel };

                // Если картинка была вшита в сам заголовок папки — нативно прописываем её тег в контент создаваемой ноды
                if (pText.Contains("<image"))
                {
                    int imgIdx = pText.IndexOf("<image");
                    headingEntry.EntryContent = pText.Substring(imgIdx).Trim();
                }

                savedEntries.Remove(match);
                correctParent.ChildEntries.Add(headingEntry);
                activeChain.Add(headingEntry);
                activePaths[targetLevel] = match.EntryPath;

                foreach (var key in activePaths.Keys.Where(k => k > targetLevel).ToList()) activePaths.Remove(key);
                return headingEntry;
            }

            currentActiveTarget = correctParent;
            activePaths[targetLevel] = fullHeadingPath;
            return currentActiveTarget;
        }
        // Б) ВЕТКА МАРКИРОВАННЫХ СПИСКОВ
        else if (currentActiveTarget != null && paragraph["bullet"] is JToken bulletToken)
        {
            string itemText = pText.Trim();
            if (string.IsNullOrEmpty(itemText)) return currentActiveTarget;

            int nestingLevel = bulletToken["nestingLevel"]?.Value<int>() ?? 0;
            if (!isInListMode)
            {
                int hash = 0; string seed = itemText + tabPath;
                unchecked { foreach (char c in seed) hash = (hash * 31) + c; }
                currentListId = $"l_{Math.Abs(hash).ToString("x4")}";
                currentActiveTarget.EntryContent += $"{Environment.NewLine}<ul id=\"{currentListId}\">";
                isInListMode = true;
            }
            currentActiveTarget.EntryContent += $"{Environment.NewLine}  <li level=\"{nestingLevel}\">{itemText}</li>";
        }
        // В) ВЕТКА ОБЫЧНОГО ХУДОЖЕСТВЕННОГО ТЕКСТА
        // ИСПРАВЛЕНО: Если этот параграф уже обработан выше как заголовок папки структуры — 
        // пропускаем его дублирование в блок обычного прозаического контента!
        else if (currentActiveTarget != null && !string.IsNullOrWhiteSpace(pText))
        {
            if (isInListMode) { currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">{Environment.NewLine}"; isInListMode = false; }
            currentActiveTarget.EntryContent += $"{Environment.NewLine}{pText.Trim()}";
        }

        return currentActiveTarget;
    }


    /// <summary>
    /// ИЗОЛИРОВАННАЯ ФУНКЦИЯ 3: Парсинг и рендеринг XML-таблиц.
    /// </summary>
    private static void ProcessCodeTable(JObject element, DocEntry currentActiveTarget, ref bool isInListMode, string currentListId)
    {
        if (isInListMode)
        {
            currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">{Environment.NewLine}";
            isInListMode = false;
        }

        currentActiveTarget.EntryContent += $"{Environment.NewLine}  <table>";
        var rows = element["table"]?["tableRows"] as JArray;

        if (rows != null)
        {
            foreach (var row in rows)
            {
                if (row == null) continue;
                currentActiveTarget.EntryContent += $"{Environment.NewLine}    <tr>";

                var cells = row["tableCells"] as JArray;
                if (cells != null)
                {
                    foreach (var cell in cells)
                    {
                        string cellText = "";
                        var cellContent = cell?["content"] as JArray;

                        if (cellContent != null)
                        {
                            var cellTextSb = new StringBuilder();
                            foreach (var cellEl in cellContent)
                            {
                                var cellElements = cellEl?["paragraph"]?["elements"] as JArray;
                                if (cellElements != null)
                                {
                                    foreach (var ce in cellElements)
                                    {
                                        cellTextSb.Append(ce?["textRun"]?["content"]?.ToString() ?? string.Empty);
                                    }
                                }
                            }
                            cellText = cellTextSb.ToString().Replace("\r", "").Replace("\n", "").Trim();
                        }
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}      <td>{cellText}</td>";
                    }
                }
                currentActiveTarget.EntryContent += $"{Environment.NewLine}    </tr>";
            }
        }
        currentActiveTarget.EntryContent += $"{Environment.NewLine}  </table>";
    }
    
    public override string ExtractImageToCache(string objectId)
    {
        if (string.IsNullOrEmpty(objectId)) return string.Empty;

        // Целевой путь: googlecache\images\{docId}
        string imagesDir = GetImagesDirectory();
        // ИСПРАВЛЕНО: Ищем файл строго с суффиксом конверсии
        string finalJpgPath = Path.Combine(imagesDir, $"{objectId}_converted.jpg");

        if (File.Exists(finalJpgPath)) return finalJpgPath;

        try
        {
            string rawDownloadedPath = string.Empty;

            if (Directory.Exists(imagesDir))
            {
                // ================================================================= -->
                // ИСПРАВЛЕНО: ТОЧНЫЙ ОТСЕВ МАРКЕРНЫХ КОПИЙ БЕЗ БЛОКИРОВКИ ИСХОДНЫХ JPG-->
                // ================================================================= -->
                // Нативно и мгновенно ищем скачанный файл по системной маске objectId.*
                var matchedFiles = Directory.GetFiles(imagesDir, $"{objectId}.*");

                // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Отсекаем СТРОГО те файлы, которые уже содержат 
                // маркер "_converted". Это позволит исходным файлам .jpg Беатрис беспрепятственно 
                // проходить на глубокую конверсию и sRGB реанимацию!
                rawDownloadedPath = matchedFiles.FirstOrDefault(f => !f.Contains("_converted", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

            }

            // 2. Если исходный скачанный файл обнаружен — копируем/нормализуем его в Baseline JPEG
            if (!string.IsNullOrEmpty(rawDownloadedPath) && File.Exists(rawDownloadedPath))
            {
                return NormalizeAndConvertImage(rawDownloadedPath, imagesDir, objectId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Google DOM Extraction Error] {objectId}: {ex.Message}");
        }

        return string.Empty;
    }


}

