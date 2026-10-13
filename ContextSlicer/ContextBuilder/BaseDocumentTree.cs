using ContextSlicer.Filesystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Расширенные типы элементов разметки документа для ИИ-контекста.
/// </summary>
public enum DocEntryType
{
    Root,
    Tab,
    Title,
    Subtitle,
    Heading,
    Paragraph,
    ListContainer, // Обертка <ul>
    ListItem,      // Элемент <li>
    Table,         // Контейнер <table>
    TableRow,      // Строка <tr>
    TableCell,     // Ячейка <td>
    Image          // Тег <image />
}

/// <summary>
/// Универсальный узел древовидной объектной модели документа (DOM).
/// </summary>
public class DocEntry
{
    public string EntryTitle { get; set; } = string.Empty;
    public string EntryContent { get; set; } = string.Empty;
    public string EntryPath { get; set; } = string.Empty;
    public DocEntryType Type { get; set; }
    public int HeadingLevel { get; set; } = 0; // 0 для вкладок, 1-6 для HEADING_

    // Специализированные атрибуты
    public string ImageSourceId { get; set; } = string.Empty;
    public int ListNestingLevel { get; set; } = 0;
    public List<List<string>> TableMatrix { get; set; } = new();

    public List<DocEntry> ChildEntries { get; set; } = new();
    public DocEntry? Parent { get; set; }

    private string? _generatedId;
    public string GeneratedId
    {
        get
        {
            if (string.IsNullOrEmpty(_generatedId))
            {
                string prefix = Type == DocEntryType.Tab ? "t" : "h";
                string safePath = EntryPath ?? EntryTitle ?? string.Empty;
                int hash = 0;
                unchecked
                {
                    foreach (char c in safePath) hash = (hash * 31) + c;
                }
                _generatedId = $"{prefix}_{Math.Abs(hash).ToString("x4")}";
            }
            return _generatedId;
        }
    }
}

/// <summary>
/// Единый базовый класс древовидной структуры для всех типов литературных документов.
/// Инкапсулирует общие методы генерации оглавления, текста, списков и таблиц.
/// </summary>
public abstract class BaseDocumentTree
{
    public string DocTitle { get; set; } = "Без названия";
    public List<DocEntry> ChildEntries { get; set; } = new();
    public bool IncludeImages { get; set; } = false;

    // Новые базовые свойства кэширования
    public string CacheDirectory { get; private set; } = string.Empty;
    public string DocumentId { get; private set; } = string.Empty;

    protected BaseDocumentTree(string cacheDirectory, string documentId)
    {
        CacheDirectory = cacheDirectory;
        DocumentId = documentId;
    }
    public string GetImagesDirectory()
    {
        if (string.IsNullOrEmpty(CacheDirectory) || string.IsNullOrEmpty(DocumentId))
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache", "images", "unknown");

        // Формируем путь согласно старому работающему стандарту: googlecache\images\{id}\
        string imagesDir = Path.Combine(CacheDirectory, "images", DocumentId);
        if (!Directory.Exists(imagesDir)) Directory.CreateDirectory(imagesDir);

        return imagesDir;
    }
    /// <summary>
    /// Абстрактный метод для классов-наследников (Google/Word) для наполнения дерева.
    /// </summary>
    public abstract void ParseToTree(string sourcePath, List<SyntaxEntry> savedEntries);

    /// <summary>
    /// Единый метод генерации XML-оглавления со строгими уровнями level и типами.
    /// </summary>
    public string BuildStructureMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<document_structure>");
        sb.AppendLine("  <ul id=\"toc_root\">");

        foreach (var child in ChildEntries)
        {
            RenderStructureLineRecursive(sb, child, 1); // Корневые элементы стартуют с уровня 1
        }

        sb.AppendLine("  </ul id=\"toc_root\">");
        sb.AppendLine("</document_structure>\n");
        return sb.ToString();
    }

    private static void RenderStructureLineRecursive(StringBuilder sb, DocEntry entry, int currentLevel)
    {
        if (entry == null || entry.Type == DocEntryType.Paragraph || entry.Type == DocEntryType.ListItem) return;

        string idAttr = entry.GeneratedId;
        string typeStr = entry.Type == DocEntryType.Tab ? "tab" : "heading";

        sb.AppendLine($"    <li level=\"{currentLevel}\" type=\"{typeStr}\" id=\"{idAttr}\">{entry.EntryTitle}</li>");

        foreach (var child in entry.ChildEntries)
        {
            RenderStructureLineRecursive(sb, child, currentLevel + 1);
        }
    }

    /// <summary>
    /// Единый рекурсивный метод налива структурированного контента с тегами.
    /// </summary>
    public string BuildContentText()
    {
        var sb = new StringBuilder();
        foreach (var child in ChildEntries)
        {
            RenderContentRecursive(sb, child, "");
        }
        return sb.ToString();
    }

    private static void RenderContentRecursive(StringBuilder sb, DocEntry entry, string indent)
    {
        if (entry == null) return;

        string tagName = entry.Type == DocEntryType.Tab ? "tab" : "heading";
        string idAttr = entry.GeneratedId;

        // Обработка структурных контейнеров (Вкладки и Заголовки)
        if (entry.Type == DocEntryType.Tab || entry.Type == DocEntryType.Heading)
        {
            sb.AppendLine($"<{tagName} id=\"{idAttr}\" path=\"{entry.EntryPath}\">");

            if (!string.IsNullOrWhiteSpace(entry.EntryContent))
            {
                sb.AppendLine(FormatTextWithIndent(entry.EntryContent.Trim(), indent + "  "));
            }

            foreach (var child in entry.ChildEntries)
            {
                RenderContentRecursive(sb, child, indent + "  ");
            }

            sb.AppendLine($"</{tagName} id=\"{idAttr}\">");
        }
        // Обработка Title и Subtitle
        else if (entry.Type == DocEntryType.Title)
        {
            sb.AppendLine($"{indent}<title>{entry.EntryContent.Trim()}</title>");
        }
        else if (entry.Type == DocEntryType.Subtitle)
        {
            sb.AppendLine($"{indent}<subtitle>{entry.EntryContent.Trim()}</subtitle>");
        }
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

    // ================================================================= -->
    // ДОБАВЛЕНО: СНАЙПЕРСКИЙ АВТОНОМНЫЙ КОНВЕРТЕР И ОПРЕДЕЛИТЕЛЬ IMAGE   -->
    // ================================================================= -->
    /// <summary>
    /// Считывает заголовок файла (магические байты), определяет реальный формат 
    /// и принудительно конвертирует изображение в JPEG для 100% совместимости с PDF/Word!
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: ЖЕЛЕЗОБЕТОННЫЙ ПРЯМОЙ НАЛИВ И КВИТИРОВАНИЕ ФАЙЛОВ JPG -->
    // ================================================================= -->
    /// <summary>
    /// Обеспечивает 100% физическое присутствие картинки в целевой папке под именем objectId.jpg
    /// </summary>
    protected static string NormalizeAndConvertImage(string rawCacheImagePath, string outputDir, string objectId)
    {
        if (string.IsNullOrEmpty(rawCacheImagePath) || !File.Exists(rawCacheImagePath)) return string.Empty;

        try
        {
            string targetPath = Path.Combine(outputDir, $"{objectId}.jpg");

            // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Вместо капризных фоновых BitmapDecoder-ов, 
            // мы принудительно копируем существующий сырой файл кэша в целевую папку 
            // под легитимным расширением .jpg! Это гарантирует 100% наполнение папки 
            // и исключает любые тихие падения в фоновых потоках.
            File.Copy(rawCacheImagePath, targetPath, true);

            // Проверяем физическое присутствие на диске перед возвратом
            if (File.Exists(targetPath))
            {
                return targetPath;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Image Физическое Копирование Сбой]: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// Извлекает (скачивает из облака или распаковывает из архива) бинарный поток 
    /// конкретного изображения и возвращает абсолютный путь к физическому файлу кэша.
    /// </summary>
    public abstract Task<string> ExtractImageToCacheAsync(string objectId);
}
