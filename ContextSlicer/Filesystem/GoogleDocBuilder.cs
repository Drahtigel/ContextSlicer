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

        public string BuildStructureMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО ЛИТЕРАТУРНОГО КОНТЕКСТА ===");
            sb.AppendLine("<document_structure>");

            // Передаем пустую строку для элементов самого верхнего уровня (уровень 0)
            foreach (var child in ChildEntries)
            {
                child.RenderStructureRecursive(sb, "");
            }

            sb.AppendLine("</document_structure>\n");
            return sb.ToString();
        }


        public string BuildContentText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== СОДЕРЖИМОЕ ВЫБРАННЫХ РАЗДЕЛОВ ===");
            foreach (var child in ChildEntries) child.RenderContentRecursive(sb, "");
            return sb.ToString();
        }

        /// <summary>
        /// Строит иерархическое C#-дерево на основе оригинальной структуры JSON-кэша Google Docs
        /// </summary>
        public void FromJson(string jsonContent, List<SyntaxEntry> savedEntries)
        {
            if (string.IsNullOrWhiteSpace(jsonContent) || savedEntries == null || savedEntries.Count == 0) return;

            var docJson = JObject.Parse(jsonContent);
            this.DocTitle = docJson["title"]?.ToString() ?? "Без названия";

            // Скан по глобальным вкладкам верхнего уровня
            if (docJson["tabs"] is JArray tabsArray)
            {
                foreach (var tabToken in tabsArray)
                {
                    var entry = DocEntry.BuildEntryRecursive(tabToken, savedEntries, "");
                    if (entry != null) this.ChildEntries.Add(entry);
                }
            }
            // Скан по плоскому телу документа, если вкладок нет
            else if (docJson["body"]?["content"] is JArray contentArray)
            {
                var entry = DocEntry.BuildEntryRecursive(docJson, savedEntries, "");
                if (entry != null) this.ChildEntries.Add(entry);
            }
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
        // ИСПРАВЛЕНО: ГАРАНТИРОВАННОЕ ДЕРЕВО ОГЛАВЛЕНИЯ ПРЯМО ПО DOCENTRY    -->
        // ================================================================= -->
        // ================================================================= -->
        // ИСПРАВЛЕНО: СТРОГОЕ ГРАФИЧЕСКОЕ ДЕРЕВО ОГЛАВЛЕНИЯ ПО КОРНЮ ДАННЫХ -->
        // ================================================================= -->
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


        public void RenderContentRecursive(StringBuilder sb, string indent)
        {
            string tagName = Type == DocEntryType.Tab ? "tab" : "heading";
            sb.AppendLine($"{indent}<{tagName} path=\"{EntryPath}\">");

            if (!string.IsNullOrWhiteSpace(EntryContent))
            {
                sb.AppendLine(FormatTextWithIndent(EntryContent.Trim(), indent + "  "));
            }

            foreach (var child in ChildEntries) child.RenderContentRecursive(sb, indent + "  ");

            sb.AppendLine($"{indent}</{tagName}>\n");
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
        public static DocEntry? BuildEntryRecursive(JToken token, List<SyntaxEntry> savedEntries, string currentPath)
        {
            DocEntry? localRoot = null;

            // СЦЕНАРИЙ А: Обработка вкладки Google Docs
            string tabTitle = token["tabProperties"]?["title"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(tabTitle))
            {
                string fullPath = string.IsNullOrEmpty(currentPath) ? tabTitle : $"{currentPath}/{tabTitle}";
                var match = savedEntries.FirstOrDefault(e => e.Type == EntryType.Tab && e.EntryPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    localRoot = new DocEntry { EntryTitle = tabTitle, EntryPath = match.EntryPath, Type = DocEntryType.Tab, HeadingLevel = 0 };
                    savedEntries.Remove(match);

                    // Сканируем вложенные подвкладки, если они есть в структуре Google API
                    if (token["childTabs"] is JArray childTabs)
                    {
                        foreach (var childTab in childTabs)
                        {
                            var childEntry = BuildEntryRecursive(childTab, savedEntries, fullPath);
                            if (childEntry != null) localRoot.ChildEntries.Add(childEntry);
                        }
                    }

                    // Сканируем контент параграфов текущей вкладки
                    var content = token["documentTab"]?["body"]?["content"] as JArray ?? token["body"]?["content"] as JArray;
                    if (content != null) ParseParagraphsToTree(content, localRoot, savedEntries, fullPath);
                }
                return localRoot;
            }

            return null;
        }

        /// <summary>
        /// Построчно обходит контент, автоматически укладывая HEADING_2 внутрь HEADING_1 по уровням иерархии
        /// </summary>
        // ================================================================= -->
        // ИСПРАВЛЕНО: ЖЕЛЕЗНЫЙ ИЕРАРХИЧЕСКИЙ СБОРЩИК КОНТЕНТА С ИЗОЛЯЦИЕЙ    -->
        // ================================================================= -->
        private static void ParseParagraphsToTree(JArray contentArray, DocEntry parentTab, List<SyntaxEntry> savedEntries, string tabPath)
        {
            // Цепочка активных объектов DocEntry для контроля вложенности (0 — корень вкладки)
            var activeChain = new List<DocEntry> { parentTab };

            // Словарь сквозных иерархических путей (0 — путь вкладки)
            var activePaths = new Dictionary<int, string> { { 0, tabPath } };

            // Ссылка на текущий активный узел для сбора текста. Если null — текст отбрасывается.
            DocEntry? currentActiveTarget = parentTab;

            foreach (var element in contentArray)
            {
                var paragraph = element["paragraph"];
                if (paragraph == null) continue;

                string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;

                // Линейно и быстро вытягиваем чистый текст текущего абзаца
                var textBuilder = new StringBuilder();
                if (paragraph["elements"] is JArray elements)
                {
                    foreach (var el in elements)
                    {
                        textBuilder.Append(el["textRun"]?["content"]?.ToString() ?? string.Empty);
                    }
                }
                string pText = textBuilder.ToString();

                // ОБНАРУЖЕН ЗАГОЛОВОК (ЖЕСТКАЯ СТЕНА РАЗДЕЛЕНИЯ КОНТЕНТА)
                if (namedStyle.StartsWith("HEADING_") && int.TryParse(namedStyle.Substring(8), out int level))
                {
                    string headingTitle = pText.Trim();
                    if (string.IsNullOrEmpty(headingTitle)) continue;

                    // ПРАВИЛО 1: Любой встреченный заголовок закрывает все текущие вложенные подразделы!
                    // Откатываем стек назад, пока не найдем узел, чей уровень строго выше нового заголовка
                    while (activeChain.Count > 1 && activeChain[activeChain.Count - 1].HeadingLevel >= level)
                    {
                        activeChain.RemoveAt(activeChain.Count - 1);
                    }

                    // На вершине стека гарантированно находится правильный родитель текущего уровня
                    DocEntry correctParent = activeChain[activeChain.Count - 1];

                    // Вычисляем путь родительского узла из словаря иерархии путей
                    int targetParentLevel = level - 1;
                    while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel))
                    {
                        targetParentLevel--;
                    }
                    string parentPath = activePaths[targetParentLevel];

                    // Формируем строгий сквозной путь, идентичный парсеру
                    string fullHeadingPath = string.IsNullOrEmpty(parentPath) ? headingTitle : $"{parentPath}/{headingTitle}";

                    // Сверяемся с чек-листом выбранных на UI элементов
                    var match = savedEntries.FirstOrDefault(e =>
                        (e.Type == EntryType.Heading || e.Type == EntryType.Section) &&
                        e.EntryPath.Equals(fullHeadingPath, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        // СЦЕНАРИЙ А: Раздел выбран пользователем. Создаем честный узел книги.
                        DocEntryType localType = match.Type == EntryType.Tab ? DocEntryType.Tab : DocEntryType.Heading;
                        var headingEntry = new DocEntry
                        {
                            EntryTitle = headingTitle,
                            EntryPath = match.EntryPath,
                            Type = localType,
                            HeadingLevel = level
                        };

                        // Удаляем из чек-листа и добавляем в ChildEntries правильного родителя
                        savedEntries.Remove(match);
                        correctParent.ChildEntries.Add(headingEntry);

                        // Фиксируем новую главу в цепочке и включаем запись прозы в неё
                        activeChain.Add(headingEntry);
                        currentActiveTarget = headingEntry;

                        // Сохраняем путь для иерархии вложенных подзаголовков
                        activePaths[level] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                    else
                    {
                        // СЦЕНАРИЙ Б: Раздел НЕ выбран на UI (Глава 5, невыбранный подпункт и т.д.)
                        // ПРАВИЛО 2: Мы наглухо выключаем приемник текста! Весь контент летит в пустоту.
                        currentActiveTarget = null;

                        // Создаем виртуальный маркер уровня для стека путей, чтобы вложенные в него подпункты 
                        // (если они вдруг чекнуты) могли правильно вычислить свой иерархический путь
                        activePaths[level] = fullHeadingPath;
                        var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                        foreach (var key in keysToRemove) activePaths.Remove(key);
                    }
                }
                else if (currentActiveTarget != null && !string.IsNullOrWhiteSpace(pText))
                {
                    // Обычный текст прозы накапливается ТОЛЬКО если приемник включен (не null)
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
        public static async Task<string> BuildContextTextAsync(List<SyntaxEntry> savedEntries, string rootPath, bool includeDirectoryStructure, CancellationToken token)
        {
            if (savedEntries == null || savedEntries.Count == 0 || string.IsNullOrWhiteSpace(rootPath)) return string.Empty;

            if (!File.Exists(rootPath)) return string.Empty;

            // 1. Вычитываем сырой JSON-кэш с диска
            string jsonContent = await File.ReadAllTextAsync(rootPath, Encoding.UTF8, token);
            var checkList = savedEntries.ToList();

            // 2. СНАЧАЛА ПОЛНОСТЬЮ СТРОИМ ДЕРЕВО КНИГИ ИЗ JSON
            var rootDocument = new RootDoc();
            rootDocument.FromJson(jsonContent, checkList);

            // 3. ТОЛЬКО ТЕПЕРЬ, КОГДА ROOTDOC ПОЛНОСТЬЮ ПОРЕЗАЛ И НАПОЛНИЛ ДАННЫЕ, СБИРАЕМ ТЕКСТ
            var finalResult = new StringBuilder();

            // Вызываем оглавление строго ПОСЛЕ парсинга по готовой структуре DocEntry!
            if (includeDirectoryStructure)
            {
                finalResult.Append(rootDocument.BuildStructureMarkdown());
            }

            // Дописываем художественный контент разделов
            finalResult.Append(rootDocument.BuildContentText());

            return finalResult.ToString();
        }

    }
}


