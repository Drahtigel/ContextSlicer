using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextSlicer;

// Типы элементов синтаксической структуры, которые мы поддерживаем
public enum EntryType
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Function, // Для методов C# и функций JS/TS
    Section   // Для HTML-тегов, CSS-селекторов и регионов
}

// Модель элемента структуры для хранения на диске и отображения в UI
public class SyntaxEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string EntryPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public EntryType Type { get; set; }
    public string SpanInfo { get; set; } = string.Empty;

    // НОВОЕ: Внутренние элементы для поддержки вложенности синтаксиса (классы в namespace, методы в классах)
    public List<SyntaxEntry> Children { get; set; } = new List<SyntaxEntry>();
}

// Общий интерфейс для всех языковых парсеров
public interface ISyntaxParser
{
    Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath);
}

// ФАБРИКА ПАРСЕРОВ: определяет, какой парсер вызвать на основе расширения файла
public static class SyntaxParserFactory
{
    private static readonly Dictionary<string, ISyntaxParser> _parsers = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".cs", new CSharpSyntaxParser() },
        { ".js", new JavaScriptSyntaxParser() },
        { ".ts", new JavaScriptSyntaxParser() }, // Используем общий JS/TS парсер
        { ".html", new HtmlSyntaxParser() },
        { ".htm", new HtmlSyntaxParser() },
        { ".php", new PhpSyntaxParser() }, // РЕГИСТРАЦИЯ PHP
        { ".sql", new SqlSyntaxParser() } // РЕГИСТРАЦИЯ SQL
    };

    public static bool IsSupported(string extension)
    {
        return _parsers.ContainsKey(extension);
    }

    public static ISyntaxParser? GetParser(string extension)
    {
        return _parsers.TryGetValue(extension, out var parser) ? parser : null;
    }

}

// 1. РЕАЛИЗАЦИЯ ДЛЯ C# (Roslyn API)
public class CSharpSyntaxParser : ISyntaxParser
{
    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        try
        {
            string code = await File.ReadAllTextAsync(absolutePath);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            if (root != null)
            {
                // Блок импортов
                if (root.Usings.Count > 0)
                {
                    int start = root.Usings[0].Span.Start;
                    int length = root.Usings[root.Usings.Count - 1].Span.End - start;

                    result.Add(new SyntaxEntry
                    {
                        FilePath = relativePath,
                        EntryPath = "Global.Usings",
                        DisplayName = "[Импорты (using)]",
                        Type = EntryType.Section,
                        SpanInfo = $"{start},{length}"
                    });
                }

                // Запускаем плоский сбор элементов
                ParseNodeFlat(root, relativePath, string.Empty, result);
            }
        }
        catch { /* Файл занят */ }
        return result;
    }

    private void ParseNodeFlat(SyntaxNode node, string relPath, string parentPath, List<SyntaxEntry> resultList)
    {
        foreach (var child in node.ChildNodes())
        {
            SyntaxEntry? entry = null;
            string currentName = string.Empty;
            bool isContainer = false;

            if (child is NamespaceDeclarationSyntax ns)
            {
                currentName = ns.Name.ToString();
                entry = new SyntaxEntry { Type = EntryType.Namespace, DisplayName = currentName };
                isContainer = true;
            }
            else if (child is FileScopedNamespaceDeclarationSyntax fsns)
            {
                currentName = fsns.Name.ToString();
                entry = new SyntaxEntry { Type = EntryType.Namespace, DisplayName = currentName };
                isContainer = true;
            }
            else if (child is ClassDeclarationSyntax cls)
            {
                currentName = cls.Identifier.Text;
                entry = new SyntaxEntry { Type = EntryType.Class, DisplayName = currentName };
                isContainer = true;
            }
            else if (child is StructDeclarationSyntax str)
            {
                currentName = str.Identifier.Text;
                entry = new SyntaxEntry { Type = EntryType.Struct, DisplayName = currentName };
                isContainer = true;
            }
            else if (child is InterfaceDeclarationSyntax itemFace)
            {
                currentName = itemFace.Identifier.Text;
                entry = new SyntaxEntry { Type = EntryType.Interface, DisplayName = currentName };
                isContainer = true;
            }
            else if (child is EnumDeclarationSyntax en)
            {
                currentName = en.Identifier.Text;
                entry = new SyntaxEntry { Type = EntryType.Enum, DisplayName = currentName };
                isContainer = true;
            }
            else if (child is MethodDeclarationSyntax method)
            {
                currentName = method.Identifier.Text;
                entry = new SyntaxEntry { Type = EntryType.Function, DisplayName = $"{currentName}()" };
            }
            else if (child is GlobalStatementSyntax gs && gs.Statement is LocalFunctionStatementSyntax lfs)
            {
                currentName = lfs.Identifier.Text;
                entry = new SyntaxEntry { Type = EntryType.Function, DisplayName = $"{currentName}() [Top-Level]" };
            }

            if (entry != null)
            {
                string fullEntryPath = string.IsNullOrEmpty(parentPath) ? currentName : $"{parentPath}.{currentName}";
                entry.FilePath = relPath;
                entry.EntryPath = fullEntryPath;
                entry.SpanInfo = $"{child.Span.Start},{child.Span.Length}";
                resultList.Add(entry);

                // Если это контейнер (класс, namespace), продолжаем сканировать ЕГО детей, прокидывая обновленный путь родителя
                if (isContainer)
                {
                    ParseNodeFlat(child, relPath, fullEntryPath, resultList);
                }
            }
            else if (child is not UsingDirectiveSyntax)
            {
                // Если узел промежуточный, просто идем глубь
                ParseNodeFlat(child, relPath, parentPath, resultList);
            }
        }
    }
}



// 2. УМНАЯ РЕАЛИЗАЦИЯ ДЛЯ JAVASCRIPT / TYPESCRIPT (С автоматическим вычислением тела функций)
public class JavaScriptSyntaxParser : ISyntaxParser
{
    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        try
        {
            string content = await File.ReadAllTextAsync(absolutePath);
            // Ищем классические функции или стрелочные константы
            var funcRegex = new Regex(@"(?:function\s+([a-zA-Z0-9_]+)|(?:const|let|var)\s+([a-zA-Z0-9_]+)\s*=\s*(?:\([^)]*\)|[a-zA-Z0-9_]+)\s*=>)", RegexOptions.Compiled);
            var matches = funcRegex.Matches(content);

            foreach (Match m in matches)
            {
                // Извлекаем имя из первой или второй группы захвата
                string name = !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;

                if (string.IsNullOrWhiteSpace(name)) continue;

                // Вычисляем реальную длину тела функции по балансу фигурных скобок
                int totalLength = FindJsBlockLength(content, m.Index);

                result.Add(new SyntaxEntry
                {
                    FilePath = relativePath,
                    EntryPath = name,
                    DisplayName = $"{name}()",
                    Type = EntryType.Function,
                    SpanInfo = $"{m.Index},{totalLength}" // ТЕПЕРЬ ПЕРЕДАЕТСЯ ПОЛНЫЙ КОД JS ФУНКЦИИ!
                });
            }
        }
        catch { }
        return result;
    }

    // Алгоритм баланса фигурных скобок для JS/TS функций
    private int FindJsBlockLength(string content, int startIndex)
    {
        int openBracePos = content.IndexOf('{', startIndex);
        if (openBracePos == -1)
        {
            // Если это стрелочная однострочная функция без фигурных скобок, забираем её до конца строки
            int endOfLine = content.IndexOf('\n', startIndex);
            return endOfLine != -1 ? (endOfLine - startIndex) : (content.Length - startIndex);
        }

        int braceCount = 1;
        int i = openBracePos + 1;

        for (; i < content.Length; i++)
        {
            if (content[i] == '{') braceCount++;
            else if (content[i] == '}')
            {
                braceCount--;
                if (braceCount == 0)
                {
                    i++; // Включаем закрывающую скобку } в диапазон
                    break;
                }
            }
        }

        if (i > content.Length) i = content.Length;
        return i - startIndex;
    }
}

// 3. УМНАЯ РЕАЛИЗАЦИЯ ДЛЯ HTML (Захват полноценных тегов вместе с их содержимым)
public class HtmlSyntaxParser : ISyntaxParser
{
    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        try
        {
            string content = await File.ReadAllTextAsync(absolutePath);
            // Находим теги с id логических блоков разметки
            var tagRegex = new Regex(@"<(section|header|footer|main|div)\s+[^>]*id=[""']([^""']+)[""'][^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var matches = tagRegex.Matches(content);

            foreach (Match m in matches)
            {
                string tagName = m.Groups[1].Value;
                string idValue = m.Groups[2].Value;

                if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(idValue)) continue;

                // Находим точную позицию закрывающего тега разметки </tag>, учитывая вложенность
                int totalLength = FindHtmlBlockLength(content, m.Index, tagName);

                result.Add(new SyntaxEntry
                {
                    FilePath = relativePath,
                    EntryPath = $"{tagName}#{idValue}",
                    DisplayName = $"<{tagName} id=\"{idValue}\">",
                    Type = EntryType.Section,
                    SpanInfo = $"{m.Index},{totalLength}" // ТЕПЕРЬ HTML ВЫГРУЖАЕТСЯ БЛОКОМ ЦЕЛИКОМ!
                });
            }
        }
        catch { }
        return result;
    }

    // Алгоритм поиска парного закрывающего HTML-тега
    private int FindHtmlBlockLength(string content, int startIndex, string tagName)
    {
        string openPattern = $"<{tagName}";
        string closePattern = $"</{tagName}>";

        int currentPos = startIndex + openPattern.Length;
        int tagCount = 1;

        while (currentPos < content.Length)
        {
            // Ищем следующее открытие или закрытие этого тега
            int nextOpen = content.IndexOf(openPattern, currentPos, StringComparison.OrdinalIgnoreCase);
            int nextClose = content.IndexOf(closePattern, currentPos, StringComparison.OrdinalIgnoreCase);

            // Если закрывающих тегов больше нет — забираем до конца файла (защита от битой разметки)
            if (nextClose == -1) return content.Length - startIndex;

            // Если нашли открытие вложенного такого же тега ДО закрытия текущего
            if (nextOpen != -1 && nextOpen < nextClose)
            {
                tagCount++;
                currentPos = nextOpen + openPattern.Length;
            }
            else
            {
                tagCount--;
                if (tagCount == 0)
                {
                    // Нашли парный закрывающий тег. Включаем его длину в диапазон
                    return (nextClose + closePattern.Length) - startIndex;
                }
                currentPos = nextClose + closePattern.Length;
            }
        }

        return content.Length - startIndex;
    }
}


// 4. ЛЕГКОВЕСНАЯ РЕАЛИЗАЦИЯ ДЛЯ PHP (Объектно-ориентированный и процедурный код)
// 4. УМНАЯ РЕАЛИЗАЦИЯ ДЛЯ PHP (С автоматическим вычислением тела функций и классов)
public class PhpSyntaxParser : ISyntaxParser
{
    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        try
        {
            string content = await File.ReadAllTextAsync(absolutePath);

            // Находит namespace, class, interface, trait и функции
            var phpRegex = new Regex(@"(?:namespace\s+([a-zA-Z0-9_\\\\]+);)|(?:(?:class|interface|trait)\s+([a-zA-Z0-9_]+))|(?:function\s+([a-zA-Z0-9_]+)\s*\()", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var matches = phpRegex.Matches(content);

            string currentParentPath = string.Empty;

            foreach (Match m in matches)
            {
                // 1. Обработка Namespace
                if (m.Groups[1].Success)
                {
                    string nsName = m.Groups[1].Value;
                    currentParentPath = nsName;
                    result.Add(new SyntaxEntry
                    {
                        FilePath = relativePath,
                        EntryPath = nsName,
                        DisplayName = nsName,
                        Type = EntryType.Namespace,
                        SpanInfo = $"{m.Index},{m.Length}"
                    });
                }
                // 2. Обработка Класса / Интерфейса
                else if (m.Groups[2].Success)
                {
                    string className = m.Groups[2].Value;
                    string fullPath = string.IsNullOrEmpty(currentParentPath) ? className : $"{currentParentPath}.{className}";
                    currentParentPath = fullPath;

                    // Вычисляем длину класса по скобкам
                    int totalLength = FindBlockLength(content, m.Index);

                    result.Add(new SyntaxEntry
                    {
                        FilePath = relativePath,
                        EntryPath = fullPath,
                        DisplayName = className,
                        Type = EntryType.Class,
                        SpanInfo = $"{m.Index},{totalLength}"
                    });
                }
                // 3. Обработка Методов и Функций PHP
                else if (m.Groups[3].Success)
                {
                    string funcName = m.Groups[3].Value;
                    string fullPath = string.IsNullOrEmpty(currentParentPath) ? funcName : $"{currentParentPath}.{funcName}";

                    // Находим реальную длину тела функции бэкенда со всеми внутренностями
                    int totalLength = FindBlockLength(content, m.Index);

                    result.Add(new SyntaxEntry
                    {
                        FilePath = relativePath,
                        EntryPath = fullPath,
                        DisplayName = $"{funcName}()",
                        Type = EntryType.Function,
                        SpanInfo = $"{m.Index},{totalLength}" // ТЕПЕРЬ ПЕРЕДАЕТСЯ ПОЛНЫЙ ВЕС ФУНКЦИИ!
                    });
                }
            }
        }
        catch { }
        return result;
    }

    // Алгоритм баланса скобок: находит конец программного блока { ... }
    private int FindBlockLength(string content, int startIndex)
    {
        int openBracePos = content.IndexOf('{', startIndex);
        if (openBracePos == -1) return content.Length - startIndex; // Если скобок нет, берем до конца строки

        int braceCount = 1;
        int i = openBracePos + 1;

        for (; i < content.Length; i++)
        {
            if (content[i] == '{') braceCount++;
            else if (content[i] == '}')
            {
                braceCount--;
                if (braceCount == 0)
                {
                    i++; // Включаем закрывающую скобку в диапазон
                    break;
                }
            }
        }

        if (i > content.Length) i = content.Length;
        return i - startIndex;
    }
}




// 5. УМНАЯ РЕАЛИЗАЦИЯ ДЛЯ SQL ФАЙЛОВ (С автоматическим вычислением тела таблиц и процедур)
public class SqlSyntaxParser : ISyntaxParser
{
    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        try
        {
            string content = await File.ReadAllTextAsync(absolutePath);

            // Ищем конструкции: CREATE TABLE имя, CREATE PROCEDURE имя, CREATE VIEW имя, CREATE FUNCTION имя
            var sqlRegex = new Regex(@"CREATE\s+(?:OR\s+ALTER\s+)?(TABLE|PROCEDURE|PROC|VIEW|FUNCTION)\s+([a-zA-Z0-9_""\.`\[\]]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var matches = sqlRegex.Matches(content);

            foreach (Match m in matches)
            {
                if (m.Groups.Count < 3) continue;

                string objectType = m.Groups[1].Value.ToUpper();
                string objectName = m.Groups[2].Value.Replace("[", "").Replace("]", "").Replace("`", "").Replace("\"", ""); // Очищаем от экранирования

                if (string.IsNullOrWhiteSpace(objectName)) continue;

                // 1. СНАЧАЛА вычисляем длину блока SQL-объекта
                int totalLength = FindSqlBlockLength(content, m.Index, objectType);

                // 2. Переводим тип в понятный для нашего дерева префикс (связываем с ключами локализации)
                EntryType type = EntryType.Section;
                string localKey = "";

                if (objectType == "PROCEDURE" || objectType == "PROC" || objectType == "FUNCTION")
                {
                    type = EntryType.Function;
                    localKey = "Str_Type_SqlFunction";
                }
                else if (objectType == "TABLE")
                {
                    type = EntryType.Section;
                    localKey = "Str_Type_SqlTable";
                }
                else if (objectType == "VIEW")
                {
                    type = EntryType.Section;
                    localKey = "Str_Type_SqlView";
                }

                // 3. Вытаскиваем локализованную строку префикса из ресурсов приложения
                string displayPrefix = $"[{objectType}]";
                if (!string.IsNullOrEmpty(localKey) && System.Windows.Application.Current.Resources.Contains(localKey))
                {
                    displayPrefix = System.Windows.Application.Current.Resources[localKey] as string ?? displayPrefix;
                }

                // 4. Безопасно добавляем запись в итоговый синтаксический список
                result.Add(new SyntaxEntry
                {
                    FilePath = relativePath,
                    EntryPath = objectName,
                    DisplayName = $"{displayPrefix} {objectName}", // Строка формируется локализованной
                    Type = type,
                    SpanInfo = $"{m.Index},{totalLength}" // Ошибка CS0103 полностью устранена
                });
            }

        }
        catch { }
        return result;
    }

    // Алгоритм определения границ SQL объектов
    private int FindSqlBlockLength(string content, int startIndex, string objectType)
    {
        if (objectType == "TABLE")
        {
            // Для таблиц ищем первую открывающую круглую скобку после слова CREATE TABLE
            int openParenthesisPos = content.IndexOf('(', startIndex);
            if (openParenthesisPos == -1) return content.Length - startIndex;

            int parenthesisCount = 1;
            int i = openParenthesisPos + 1;

            // Идем по тексту и считаем баланс круглых скобок (учитывая вложенные типы данных, например DECIMAL(10,2))
            for (; i < content.Length; i++)
            {
                if (content[i] == '(') parenthesisCount++;
                else if (content[i] == ')')
                {
                    parenthesisCount--;
                    if (parenthesisCount == 0)
                    {
                        // Нашли закрывающую скобку таблицы. Продвигаемся чуть дальше, чтобы захватить точку с запятой, если она есть
                        i++;
                        if (i < content.Length && content[i] == ';') i++;
                        break;
                    }
                }
            }
            if (i > content.Length) i = content.Length;
            return i - startIndex;
        }
        else
        {
            // Для процедур, функций и представлений забираем текст до следующей точки с запятой 
            // или до начала следующего оператора CREATE (что наступит раньше)
            int nextSemicolon = content.IndexOf(';', startIndex + 10);

            // Проверяем, нет ли поблизости следующего оператора CREATE
            var nextCreateMatch = Regex.Match(content.Substring(startIndex + 10), @"\bCREATE\b", RegexOptions.IgnoreCase);
            int nextCreatePos = nextCreateMatch.Success ? startIndex + 10 + nextCreateMatch.Index : -1;

            int endPos = content.Length;

            if (nextSemicolon != -1 && nextCreatePos != -1)
                endPos = Math.Min(nextSemicolon + 1, nextCreatePos);
            else if (nextSemicolon != -1)
                endPos = nextSemicolon + 1;
            else if (nextCreatePos != -1)
                endPos = nextCreatePos;

            return endPos - startIndex;
        }
    }
}



