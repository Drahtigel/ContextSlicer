using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ContextSlicer.Filesystem;

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
                // Сначала вытаскиваем и инжектируем картинки, если они есть
                ProcessCodeInlineImages(paragraph, currentActiveTarget);

                // Затем обрабатываем текст, заголовки и списки
                // ================================================================= -->
                // ИСПРАВЛЕНО CS1615: УДАЛЕН ЛИШНИЙ МОДИФИКАТОР REF ПРИ ВЫЗОВЕ МЕТОДА-->
                // ================================================================= -->
                // Затем обрабатываем текст, заголовки и списки
                currentActiveTarget = ProcessCodeParagraph(paragraph, savedEntries, tabPath,
                    activeChain, activePaths, currentActiveTarget, ref isInListMode, ref currentListId);

            }
            else if (element["table"] != null && currentActiveTarget != null)
            {
                ProcessCodeTable(element, currentActiveTarget, ref isInListMode, currentListId);
            }
        }
    }

    /// <summary>
    /// ИЗОЛИРОВАННАЯ ФУНКЦИЯ 1: Автономный перехват и добавление картинок как узлов DOM-дерева.
    /// </summary>
    private void ProcessCodeInlineImages(JObject paragraph, DocEntry? currentActiveTarget)
    {
        if (!IncludeImages || currentActiveTarget == null || paragraph["elements"] is not JArray elements) return;

        int imageIndex = -1;
        foreach (var el in elements)
        {
            if (el?["inlineObjectElement"]?["inlineObjectId"] is not JToken imgToken) continue;

            string imgId = imgToken.ToString();
            if (string.IsNullOrEmpty(imgId)) continue;

            imageIndex++;
            string altName = currentActiveTarget.EntryTitle;
            if (imageIndex > 0) altName = $"{altName} [{imageIndex}]";

            string googleCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
            string rawImagePath = Path.Combine(googleCacheDir, imgId);

            string convertedJpgPath = NormalizeAndConvertImage(rawImagePath, googleCacheDir, imgId);

            if (!string.IsNullOrEmpty(convertedJpgPath) && File.Exists(convertedJpgPath))
            {
                var imgEntry = new DocEntry
                {
                    EntryTitle = altName,
                    EntryContent = $"<image src=\"{imgId}.jpg\" alt=\"{altName}\" />",
                    Type = DocEntryType.Paragraph,
                    HeadingLevel = currentActiveTarget.HeadingLevel + 1
                };
                currentActiveTarget.ChildEntries.Add(imgEntry);
            }
        }
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: СНАЙПЕРСКИЙ НАЛИВ КАРТИНОК ИЗ ИЕРАРХИЧЕСКОЙ ПАПКИ ИДОМ -->
    // ================================================================= -->
    private DocEntry? ProcessCodeParagraph(JObject paragraph, List<SyntaxEntry> savedEntries, string tabPath,
        List<DocEntry> activeChain, Dictionary<int, string> activePaths, DocEntry? currentActiveTarget,
        ref bool isInListMode, ref string currentListId)
    {
        string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;
        var textBuilder = new StringBuilder();

        if (paragraph["elements"] is JArray elements)
        {
            int imageIndex = -1;
            foreach (var el in elements)
            {
                if (el == null) continue;

                if (el["textRun"]?["content"] is JToken textToken)
                {
                    textBuilder.Append(textToken.ToString());
                }
                // ================================================================= -->
                // ИСПРАВЛЕНО: АСИНХРОННЫЙ ВЫЗОВ ЭКСТРАКТОРА ВНУТРИ ЦИКЛА ОБХОДА     -->
                // ================================================================= -->
                else if (IncludeImages && el["inlineObjectElement"]?["inlineObjectId"] is JToken imgToken)
                {
                    string imgId = imgToken.ToString();
                    if (!string.IsNullOrEmpty(imgId))
                    {
                        imageIndex++;
                        string altName = currentActiveTarget != null ? currentActiveTarget.EntryTitle : "Изображение";
                        if (imageIndex > 0) altName = $"{altName} [{imageIndex}]";

                        // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Не угадываем пути, а требуем от экстрактора 
                        // гарантированно достать файл с сервера прямо сейчас! Метод CalculateSizeAsync / BuildTextContentAsync 
                        // теперь работают через Task, поэтому мы можем использовать .GetAwaiter().GetResult() внутри синхронного ProcessCodeParagraph
                        string convertedJpgPath = ExtractImageToCacheAsync(imgId).GetAwaiter().GetResult();

                        if (!string.IsNullOrEmpty(convertedJpgPath) && File.Exists(convertedJpgPath))
                        {
                            textBuilder.Append($"{Environment.NewLine}{Environment.NewLine}<image src=\"{Path.GetFileName(convertedJpgPath)}\" alt=\"{altName}\" />{Environment.NewLine}");
                        }
                    }
                }

            }
        }

        string pText = textBuilder.ToString();
        string headingTitle = pText.Trim();

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

                if (pText.Contains("<image"))
                {
                    int imgIdx = pText.IndexOf("<image");
                    headingEntry.EntryContent = pText.Substring(imgIdx);
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
        else if (currentActiveTarget != null && !string.IsNullOrWhiteSpace(pText))
        {
            if (isInListMode) { currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">{Environment.NewLine}"; isInListMode = false; }
            currentActiveTarget.EntryContent += $"{Environment.NewLine}{pText}";
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
    // ================================================================= -->
    // ИСПРАВЛЕНО: ЧЕСТНЫЙ ИЕРАРХИЧЕСКИЙ ЭКСТРАКТОР КАРТИНОК ИЗ ОБЛАКА    -->
    // ================================================================= -->
    public override async Task<string> ExtractImageToCacheAsync(string objectId)
    {
        if (string.IsNullOrEmpty(objectId)) return string.Empty;

        // Папка кэша картинок согласно твоему даунлоадеру: googlecache\images\{docId}
        string imagesDir = GetImagesDirectory();
        string finalJpgPath = Path.Combine(imagesDir, $"{objectId}.jpg");

        // 1. Быстрый фолбэк: Если файл уже нормализован в JPEG ранее — мгновенно отдаем его
        if (File.Exists(finalJpgPath)) return finalJpgPath;

        try
        {
            string rawDownloadedPath = string.Empty;

            if (Directory.Exists(imagesDir))
            {
                // Нативно и быстро ищем скачанный файл на диске по маске objectId.*
                var matchedFiles = Directory.GetFiles(imagesDir, $"{objectId}.*");
                rawDownloadedPath = matchedFiles.FirstOrDefault(f => !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }

            // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Если файла на диске нет, докачиваем его силами 
            // твоего оригинального GoogleDownloader, используя честный маппинг ключей из MainViewModel!
            if (string.IsNullOrEmpty(rawDownloadedPath))
            {
                if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel vm && vm.SelectedProject != null)
                {
                    string fullProjectUrl = vm.SelectedProject.RootPath; // Честный URL документа Google Docs

                    // Вытаскиваем Email сервисного аккаунта напрямую из конфигурации выбранного проекта
                    string serviceAccountEmail = vm.SelectedProject.GoogleApiKey;
                    if (string.IsNullOrWhiteSpace(serviceAccountEmail))
                    {
                        serviceAccountEmail = Properties.Settings.Default.DefaultServiceAccountEmail;
                    }

                    // ИСПРАВЛЕНО: Переводим Email в честный физический путь к JSON-файлу на диске через твой метод!
                    string keyPath = vm.GetKeyPathByEmail(serviceAccountEmail);

                    if (!string.IsNullOrEmpty(fullProjectUrl) && File.Exists(keyPath))
                    {
                        var downloader = new Google.GoogleDownloader();

                        // Вызываем ОРИГИНАЛЬНЫЙ метод скачивания пакета картинок целиком.
                        // Он сам внутри себя выполнит JWT-авторизацию по keyPath, скачает файлы и наполнит папку!
                        await downloader.DownloadToCacheAsync(fullProjectUrl, this.CacheDirectory, keyPath);
                    }
                }

                // Повторно проверяем папку по маске после работы штатного загрузчика
                if (Directory.Exists(imagesDir))
                {
                    var matchedFiles = Directory.GetFiles(imagesDir, $"{objectId}.*");
                    rawDownloadedPath = matchedFiles.FirstOrDefault(f => !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                }
            }

            // 3. Если исходный скачанный файл обнаружен — копируем/нормализуем его в Baseline JPEG
            if (!string.IsNullOrEmpty(rawDownloadedPath) && File.Exists(rawDownloadedPath))
            {
                return NormalizeAndConvertImage(rawDownloadedPath, imagesDir, objectId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Google DOM Safe Extraction Error] {objectId}: {ex.Message}");
        }

        return string.Empty;
    }


}

