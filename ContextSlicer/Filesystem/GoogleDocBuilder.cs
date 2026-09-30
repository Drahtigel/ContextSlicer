using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ContextSlicer.Filesystem
{
    public enum DocEntryType
    {
        Tab,
        Heading,
        Section,
        TextFragment
    }

    public class RootDoc
    {
        public string DocTitle { get; set; } = string.Empty;
        public List<DocEntry> ChildEntries { get; set; } = new List<DocEntry>();

        // ИСПРАВЛЕНО: Явное свойство конфигурации картинок для корневого документа
        public bool IncludeImages { get; set; } = false;

        public void FromJson(string jsonContent, List<SyntaxEntry> savedEntries)
        {
            if (string.IsNullOrWhiteSpace(jsonContent) || savedEntries == null || savedEntries.Count == 0) return;

            var docJson = JObject.Parse(jsonContent);
            this.DocTitle = docJson["title"]?.ToString() ?? "Без названия";

            if (docJson["tabs"] is JArray tabsArray)
            {
                foreach (var tabToken in tabsArray)
                {
                    // Передаем флаг IncludeImages во внутренний рекурсивный фабричный метод
                    var entry = DocEntry.BuildEntryRecursive(tabToken, savedEntries, "", this.IncludeImages);
                    if (entry != null) this.ChildEntries.Add(entry);
                }
            }
            else if (docJson["body"]?["content"] is JArray contentArray)
            {
                var entry = DocEntry.BuildEntryRecursive(docJson, savedEntries, "", this.IncludeImages);
                if (entry != null) this.ChildEntries.Add(entry);
            }
        }

        // ================================================================= -->
        // ИСПРАВЛЕНО: ГЕНЕРАЦИЯ СТРУКТУРЫ ОГЛАВЛЕНИЯ В HTML-ФОРМАТЕ С ID    -->
        // ================================================================= -->
        public string BuildStructureMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<document_structure>");

            // Открываем монолитный контейнер структуры
            sb.AppendLine("  <ul id=\"toc_root\">");

            // Запускаем рекурсивный обход элементов дерева для построения лесенки оглавления
            foreach (var child in ChildEntries)
            {
                RenderStructureLineRecursive(sb, child, 0);
            }

            sb.AppendLine("  </ul id=\"toc_root\">");
            sb.AppendLine("</document_structure>\n");

            return sb.ToString();
        }

        // ================================================================= -->
        // ИСПРАВЛЕНО: ГЕНЕРАЦИЯ ОГЛАВЛЕНИЯ С АТРИБУТОМ ТИПА УЗЛА TYPE      -->
        // ================================================================= -->
        /// <summary>
        /// Вспомогательный рекурсивный метод сборки элементов оглавления с уровнями вложенности и типом
        /// </summary>
        private static void RenderStructureLineRecursive(StringBuilder sb, DocEntry entry, int currentLevel)
        {
            if (entry == null) return;

            // Вытаскиваем уникальный международный ID
            string idAttr = entry.GeneratedId;

            // Интеллектуально определяем строковый тип узла строго для оглавления (tab или heading)
            string typeStr = entry.Type == DocEntryType.Tab ? "tab" : "heading";

            // ИСПРАВЛЕНО: Добавлен атрибут type="..." для идеальной объектной классификации внутри ИИ
            sb.AppendLine($"    <li level=\"{currentLevel}\" type=\"{typeStr}\" id=\"{idAttr}\">{entry.EntryTitle}</li>");

            // Спускаемся по дереву к вложенным элементам структуры
            foreach (var child in entry.ChildEntries)
            {
                RenderStructureLineRecursive(sb, child, currentLevel + 1);
            }
        }

        /// <summary>
        /// Собирает монолитное текстовое полотно книги с тегами разметки
        /// </summary>
        public string BuildContentText()
        {
            var sb = new StringBuilder();
           
            foreach (var child in ChildEntries)
            {
                child.RenderContentRecursive(sb, "");
            }
            return sb.ToString();
        }

    }


    public class DocEntry
    {
        public string EntryTitle { get; set; } = string.Empty;
        public string EntryContent { get; set; } = string.Empty;
        public string EntryPath { get; set; } = string.Empty;
        public DocEntryType Type { get; set; }
        public int HeadingLevel { get; set; } = 0; // 0 для вкладок, 1-6 для HEADING_
        public List<DocEntry> ChildEntries { get; set; } = new List<DocEntry>();
       
        // ================================================================= -->
        // ИСПРАВЛЕНО: БЕЗОПАСНЫЙ UNCHECKED-ВЫЧИСЛИТЕЛЬ ХЭША БЕЗ ЗАВИСАНИЙ  -->
        // ================================================================= -->
        private string? _generatedId;
        public string GeneratedId
        {
            get
            {
                if (string.IsNullOrEmpty(_generatedId))
                {
                    string prefix = Type == DocEntryType.Tab ? "t" : "h";
                    string safePath = EntryPath ?? EntryTitle ?? string.Empty;

                    // ЖЕСТКОЕ ТАБУ НА OVERFLOWEXCEPTION: Оборачиваем цикл в unchecked блок.
                    // Теперь переполнение знакового int будет молча игнорироваться,
                    // обеспечивая мгновенную скорость и 0% нагрузки на процессор.
                    int hash = 0;
                    unchecked
                    {
                        foreach (char c in safePath)
                        {
                            hash = (hash * 31) + c;
                        }
                    }

                    // Переводим полученное число в короткую 4-значную hex-строку
                    string hashPart = Math.Abs(hash).ToString("x4");

                    _generatedId = $"{prefix}_{hashPart}";
                }
                return _generatedId;
            }
        }

        public void RenderStructureRecursive(StringBuilder sb, string indent)
        {
            // Префикс "[Вкладка]" пишется ТОЛЬКО для корневых вкладок самого верхнего уровня (где indent пустой)
            string prefix = string.IsNullOrEmpty(indent) ? $"[Вкладка] " : "├─ ";

            // Печатаем имя узла. Уровень вложенности железно определяется накопившейся строкой indent!
            sb.AppendLine($"{indent}{prefix}{EntryTitle}");

            // Каждому дочернему элементу (будь то подвкладка или глава) передаем сдвиг вправо на 4 пробела
            string nextIndent = indent + "│";
            foreach (var child in ChildEntries)
            {
                child.RenderStructureRecursive(sb, nextIndent);
            }
        }


        /// <summary>
        /// Рекурсивно собирает XML-структуру книги с симметричными международными ID
        /// </summary>
        // ================================================================= -->
        // ИСПРАВЛЕНО: ГАРАНТИРОВАННЫЙ ВЫВОД МЕЖДУНАРОДНЫХ ID ДЛЯ ВСЕХ ФОРМАТОВ -->
        // ================================================================= -->
        public void RenderContentRecursive(StringBuilder sb, string indent)
        {
            // Определяем имя XML-тега (tab или heading)
            string tagName = Type == DocEntryType.Tab ? "tab" : "heading";

            // Вызываем ленивую генерацию хэша пути (работает автономно и всегда!)
            string idAttr = GeneratedId;

            // Открывающий тег пишется строго с начала строки без пробелов для StartsWith-парсера PDF
            sb.AppendLine($"<{tagName} id=\"{idAttr}\" path=\"{EntryPath}\">");

            if (!string.IsNullOrWhiteSpace(EntryContent))
            {
                // Художественный текст форматируем с отступом для наглядности
                sb.AppendLine(FormatTextWithIndent(EntryContent.Trim(), indent + "  "));
            }

            // Рекурсивно обходим дочерние элементы дерева глав
            foreach (var child in ChildEntries)
            {
                child.RenderContentRecursive(sb, indent + "  ");
            }

            // ЗАКРЫВАЮЩИЙ ТЕГ: Симметрично запечатываем ID, спасая окно внимания DeepSeek на любом языке мира!
            sb.AppendLine($"</{tagName} id=\"{idAttr}\">");
        }

        private static string FormatTextWithIndent(string text, string indent)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i])) lines[i] = indent + lines[i];
            }
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Универсальное ядро: рекурсивно строит дерево контента, сопоставляя JSON-токены с savedEntries
        /// </summary>
        public static DocEntry? BuildEntryRecursive(JToken token, List<SyntaxEntry> savedEntries, string currentPath, bool includeImages)
        {
            DocEntry? localRoot = null;

            string tabTitle = token["tabProperties"]?["title"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(tabTitle))
            {
                string fullPath = string.IsNullOrEmpty(currentPath) ? tabTitle : $"{currentPath}/{tabTitle}";
                var match = savedEntries.FirstOrDefault(e => e.Type == EntryType.Tab && e.EntryPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    localRoot = new DocEntry { EntryTitle = tabTitle, EntryPath = match.EntryPath, Type = DocEntryType.Tab, HeadingLevel = 0 };
                    savedEntries.Remove(match);

                    if (token["childTabs"] is JArray childTabs)
                    {
                        foreach (var childTab in childTabs)
                        {
                            var childEntry = BuildEntryRecursive(childTab, savedEntries, fullPath, includeImages);
                            if (childEntry != null) localRoot.ChildEntries.Add(childEntry);
                        }
                    }

                    var content = token["documentTab"]?["body"]?["content"] as JArray ?? token["body"]?["content"] as JArray;
                    // Передаем флаг includeImages в парсер параграфов вкладки
                    if (content != null) ParseParagraphsToTree(content, localRoot, savedEntries, fullPath, includeImages);
                }
                return localRoot;
            }
            return null;
        }

        // ================================================================= -->
        // ИСПРАВЛЕНО: СБОР СПИСКОВ С УНИКАЛЬНЫМИ МЕЖДУНАРОДНЫМИ ID ДЛЯ UL   -->
        // ================================================================= -->
        private static void ParseParagraphsToTree(JArray contentArray, DocEntry parentTab, List<SyntaxEntry> savedEntries, string tabPath, bool includeImages)
        {
            var activeChain = new List<DocEntry> { parentTab };
            var activePaths = new Dictionary<int, string> { { 0, tabPath } };
            DocEntry? currentActiveTarget = parentTab;

            // Флаг состояния списка и сквозной хэш текущего контейнера
            bool isInListMode = false;
            string currentListId = "l_generic";

            foreach (var element in contentArray)
            {
                var paragraph = element["paragraph"];
                if (paragraph == null) continue;

                string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;

                var textBuilder = new StringBuilder();
                if (paragraph["elements"] is JArray elements)
                {
                    foreach (var el in elements)
                    {
                        if (el["textRun"]?["content"] is JToken textToken)
                        {
                            textBuilder.Append(textToken.ToString());
                        }
                        else if (includeImages && el["inlineObjectElement"]?["inlineObjectId"] is JToken imgToken)
                        {
                            string imgId = imgToken.ToString();
                            if (!string.IsNullOrEmpty(imgId))
                            {
                                textBuilder.Append($"{Environment.NewLine}<image src=\"{imgId}.png\" />{Environment.NewLine}");
                            }
                        }
                    }
                }
                string pText = textBuilder.ToString();

                // ОБНАРУЖЕН ЗАГОЛОВОК СТРУКТУРЫ
                if (namedStyle.StartsWith("HEADING_") && int.TryParse(namedStyle.Substring(8), out int level))
                {
                    // Если перед заголовком шёл список — гарантированно закрываем его с нужным ID
                    if (isInListMode && currentActiveTarget != null)
                    {
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">";
                        isInListMode = false;
                    }

                    string headingTitle = pText.Trim();
                    if (string.IsNullOrEmpty(headingTitle)) continue;

                    while (activeChain.Count > 1 && activeChain[activeChain.Count - 1].HeadingLevel >= level)
                    {
                        activeChain.RemoveAt(activeChain.Count - 1);
                    }

                    DocEntry correctParent = activeChain[activeChain.Count - 1];
                    int targetParentLevel = level - 1;
                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel)) targetParentLevel--;

                    string parentPath = activePaths[targetParentLevel];
                    string fullHeadingPath = string.IsNullOrEmpty(parentPath) ? headingTitle : $"{parentPath}/{headingTitle}";

                    var match = savedEntries.FirstOrDefault(e =>
                        (e.Type == EntryType.Heading || e.Type == EntryType.Section) &&
                        e.EntryPath.Equals(fullHeadingPath, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        DocEntryType localType = match.Type == EntryType.Tab ? DocEntryType.Tab : DocEntryType.Heading;
                        var headingEntry = new DocEntry { EntryTitle = headingTitle, EntryPath = match.EntryPath, Type = localType, HeadingLevel = level };

                        savedEntries.Remove(match);
                        correctParent.ChildEntries.Add(headingEntry);
                        activeChain.Add(headingEntry);
                        currentActiveTarget = headingEntry;

                        activePaths[level] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                    else
                    {
                        currentActiveTarget = null;
                        activePaths[level] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                }
                // ОБРАБОТКА ЭЛЕМЕНТОВ СТРУКТУРИРОВАННОГО СПИСКА
                else if (currentActiveTarget != null && paragraph["bullet"] is JToken bulletToken)
                {
                    string itemText = pText.Trim();
                    if (string.IsNullOrEmpty(itemText)) continue;

                    int nestingLevel = bulletToken["nestingLevel"]?.Value<int>() ?? 0;

                    // Если это первый элемент списка — вычисляем уникальный детерминированный ID контейнера
                    if (!isInListMode)
                    {
                        int hash = 0;
                        string seed = itemText + (tabPath ?? string.Empty);
                        unchecked
                        {
                            foreach (char c in seed) hash = (hash * 31) + c;
                        }
                        currentListId = $"l_{Math.Abs(hash).ToString("x4")}";

                        // Открываем тег со строгим международным ID
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}<ul id=\"{currentListId}\">";
                        isInListMode = true;
                    }

                    // Заворачиваем элемент в HTML-тег <li>
                    currentActiveTarget.EntryContent += $"{Environment.NewLine}  <li level=\"{nestingLevel}\">{itemText}</li>";
                }
                // ОБРАБОТКА ОБЫЧНОГО ХУДОЖЕСТВЕННОГО ТЕКСТА
                else if (currentActiveTarget != null && !string.IsNullOrWhiteSpace(pText))
                {
                    // Если список закончился, а пошла обычная проза — симметрично закрываем тег <ul> с его ID
                    if (isInListMode)
                    {
                        currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">{Environment.NewLine}";
                        isInListMode = false;
                    }

                    currentActiveTarget.EntryContent += pText;
                }
            }

            // Запечатываем открытый список на выходе, если глава завершилась элементом списка
            if (isInListMode && currentActiveTarget != null)
            {
                currentActiveTarget.EntryContent += $"{Environment.NewLine}</ul id=\"{currentListId}\">";
            }
        }


    }

    public static class GoogleDocBuilder
    {
        // ================================================================= -->
        // ИСПРАВЛЕНО: СТРОГАЯ ПОСЛЕДОВАТЕЛЬНОСТЬ ВЫЗОВОВ ПОСЛЕ ПАРСИНГА     -->
        // ================================================================= -->
        public static async Task<string> BuildContextTextAsync(
          List<SyntaxEntry> savedEntries,
          string rootPath,
          bool includeDirectoryStructure,
          CancellationToken token)
        {
            // Перенаправляем вызов в новую перегрузку, передавая false для картинок
            return await BuildContextTextAsync(
                savedEntries,
                rootPath,
                includeDirectoryStructure,
                false,
                token);
        }
        public static async Task<string> BuildContextTextAsync(
            List<SyntaxEntry> savedEntries,
            string rootPath,
            bool includeDirectoryStructure,
            bool includeImages,
            CancellationToken token)
        {
            if (savedEntries == null || savedEntries.Count == 0 || string.IsNullOrWhiteSpace(rootPath) || !File.Exists(rootPath))
                return string.Empty;

            // Вычитываем сырой кэш JSON романа с диска
            string jsonContent = await File.ReadAllTextAsync(rootPath, Encoding.UTF8, token);
            var checkList = savedEntries.ToList();

            // Создаем эталонное дерево книги
            var rootDocument = new RootDoc();

            // Накатываем флаг картинок на корень перед запуском рекурсивного парсинга JSON!
            rootDocument.IncludeImages = includeImages;
            rootDocument.FromJson(jsonContent, checkList);

            var finalResult = new StringBuilder();

            // Собираем оглавление с красивыми палочками
            if (includeDirectoryStructure)
            {
                finalResult.Append(rootDocument.BuildStructureMarkdown());
            }

            // Наливаем художественную прозу и текстовые маркеры картинок
            finalResult.Append(rootDocument.BuildContentText());

            return finalResult.ToString();
        }
    }
}


