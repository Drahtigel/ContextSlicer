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
        // ИСПРАВЛЕНО: МЕТОДЫ ВЫВОДА СТРУКТУРЫ И КОНТЕНТА ДЛЯ КЛАССА ROOTDOC -->
        // ================================================================= -->
        /// <summary>
        /// Генерирует XML-структуру оглавления по готовому дереву объектов
        /// </summary>
        public string BuildStructureMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО ЛИТЕРАТУРНОГО КОНТЕКСТА ===");
            sb.AppendLine("<document_structure>");
            foreach (var child in ChildEntries)
            {
                child.RenderStructureRecursive(sb, "");
            }
            sb.AppendLine("</document_structure>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Собирает монолитное текстовое полотно книги с тегами разметки
        /// </summary>
        public string BuildContentText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== СОДЕРЖИМОЕ ВЫБРАННЫХ РАЗДЕЛОВ ===");
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


        // ================================================================= -->
        // ИСПРАВЛЕНО: КОРРЕКТНЫЙ РЕНДЕРИНГ КОНТЕНТА УЗЛОВ С МАРКЕРАМИ КАРТИНОК -->
        // ================================================================= -->
        public void RenderContentRecursive(StringBuilder sb, string indent)
        {
            // Определяем имя XML-тега на основе реального системного типа узла
            string tagName = Type == DocEntryType.Tab ? "tab" : "heading";

            // Пишем открывающий тег с путём для разметки
            sb.AppendLine($"{indent}<{tagName} path=\"{EntryPath}\">");

            // Если у текущего узла дерева (вкладки или главы) есть текст или маркеры картинок
            if (!string.IsNullOrWhiteSpace(EntryContent))
            {
                // Форматируем текст с правильным ступенчатым отступом и добавляем в общий пул
                sb.AppendLine(FormatTextWithIndent(EntryContent.Trim(), indent + "  "));
            }

            // Рекурсивно спускаемся по дереву к дочерним элементам (вложенным карточкам или подпунктам)
            foreach (var child in ChildEntries)
            {
                child.RenderContentRecursive(sb, indent + "  ");
            }

            // Закрываем XML-тег текущего узла
            sb.AppendLine($"{indent}</{tagName}>");
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
        // ИСПРАВЛЕНО: ОТКАЗОУСТОЙЧИВЫЙ ИЕРАРХИЧЕСКИЙ СБОРЩИК КОНТЕНТА КНИГИ -->
        // ================================================================= -->
        private static void ParseParagraphsToTree(JArray contentArray, DocEntry parentTab, List<SyntaxEntry> savedEntries, string tabPath, bool includeImages)
        {
            // Список-цепочка текущих активных разделов (0 — корень вкладки)
            var activeChain = new List<DocEntry> { parentTab };
            var activePaths = new Dictionary<int, string> { { 0, tabPath } };

            // Приемник текста по умолчанию привязан к родительской вкладке
            DocEntry? currentActiveTarget = parentTab;

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
                                // Служебные теги всегда пишем строго с начала строки без пробелов для StartsWith парсера PDF
                                textBuilder.Append($"{Environment.NewLine}<image src=\"{imgId}.png\" />{Environment.NewLine}");
                            }
                        }
                    }
                }
                string pText = textBuilder.ToString();

                // ОБНАРУЖЕН ЗАГОЛОВОК СТРУКТУРЫ
                if (namedStyle.StartsWith("HEADING_") && int.TryParse(namedStyle.Substring(8), out int level))
                {
                    string headingTitle = pText.Trim();
                    if (string.IsNullOrEmpty(headingTitle)) continue;

                    // Закрываем в иерархии стека все старые подразделы, чей уровень ниже или равен новому
                    while (activeChain.Count > 1 && activeChain[activeChain.Count - 1].HeadingLevel >= level)
                    {
                        activeChain.RemoveAt(activeChain.Count - 1);
                    }

                    DocEntry correctParent = activeChain[activeChain.Count - 1];

                    // Вычисляем сквозной путь родительского элемента
                    int targetParentLevel = level - 1;
                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel))
                    {
                        targetParentLevel--;
                    }
                    string parentPath = activePaths[targetParentLevel];

                    // Собираем каскадный fullHeadingPath в точности по правилам GoogleDocSyntaxParser
                    string fullHeadingPath = string.IsNullOrEmpty(parentPath) ? headingTitle : $"{parentPath}/{headingTitle}";

                    // Проверяем наличие заголовка в чек-листе выбранных на UI элементов
                    var match = savedEntries.FirstOrDefault(e =>
                        (e.Type == EntryType.Heading || e.Type == EntryType.Section) &&
                        e.EntryPath.Equals(fullHeadingPath, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        // СЦЕНАРИЙ А: Раздел выбран на UI. Создаем легитимный XML-контейнер.
                        DocEntryType localType = match.Type == EntryType.Tab ? DocEntryType.Tab : DocEntryType.Heading;
                        var headingEntry = new DocEntry
                        {
                            EntryTitle = headingTitle,
                            EntryPath = match.EntryPath,
                            Type = localType,
                            HeadingLevel = level
                        };

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
                        // СЦЕНАРИЙ Б: Раздел НЕ выбран пользователем на UI (Глава 5, скрытый подпункт и т.д.)
                        // ЖЕЛЕЗНОЕ ПРАВИЛО: Полностью выключаем приемник текста. Весь последующий контент летит в пустоту!
                        currentActiveTarget = null;

                        // Фиксируем путь виртуального маркера уровня, чтобы вложенные в него чекнутые элементы 
                        // (если они есть) могли без ошибок вычислить свой правильный каскадный fullHeadingPath
                        activePaths[level] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }

                }
                else if (currentActiveTarget != null && !string.IsNullOrWhiteSpace(pText))
                {
                    currentActiveTarget.EntryContent += pText;
                }
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


