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
    // ================================================================= -->
    // ИСПРАВЛЕНО: ТОЧНЫЙ КАНWeight ПУТЬ К ИЗОБРАЖЕНИЯМ ВНУТРИ GOOGLECACHE -->
    // ================================================================= -->
    public string GetImagesDirectory()
    {
        // ИСПРАВЛЕНО: Картинки всегда лежат по строгому пути: googlecache\images\{DocumentId}
        string baseCacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "googlecache");
        string imagesDir = Path.Combine(baseCacheDir, "images", DocumentId);

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
    // ИСПРАВЛЕНО: ТРЕХЭТАПНЫЙ НЕУЯЗВИМЫЙ КАСКАД КОНВЕРСИИ КАРТИНОК С GDI+ -->
    // ================================================================= -->
    public static string NormalizeAndConvertImage(string rawCacheImagePath, string outputDir, string objectId)
    {
        if (string.IsNullOrEmpty(rawCacheImagePath) || !File.Exists(rawCacheImagePath)) return string.Empty;
        //kix.kcbc4ihbg7ia
        string finalJpgName = $"{objectId}_converted.jpg";
        string targetPath = Path.Combine(outputDir, finalJpgName);

        if (Path.GetFileName(rawCacheImagePath).Equals(finalJpgName, StringComparison.OrdinalIgnoreCase))
        {
            return rawCacheImagePath;
        }

        // ЭТАП 1: Попытка каноничной конвертации через BitmapDecoder (WPF)
        try
        {
            using (var stream = new FileStream(rawCacheImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                if (decoder != null && decoder.Frames.Count > 0)
                {
                    var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
                    encoder.Frames.Add(decoder.Frames[0]);
                    using (var os = new FileStream(targetPath, FileMode.Create, FileAccess.Write)) { encoder.Save(os); }

                    if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0) return targetPath;
                }
            }
        }
        catch { }

        // ЭТАП 2: Аварийный попиксельный sRGB фолбэк через BitmapImage (WPF)
        try
        {
            var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
            using (var stream = new FileStream(rawCacheImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
            }

            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapImage));
            using (var os = new FileStream(targetPath, FileMode.Create, FileAccess.Write)) { encoder.Save(os); }

            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0) return targetPath;
        }
        catch { }

        // ================================================================= -->
        // ЭТАП 3: ТЯЖЕЛАЯ АРТИЛЛЕРИЯ (GDI+) — СПАСЕНИЕ ПРОБЛЕМНЫХ JPEG/CMYK -->
        // ================================================================= -->
        // Если WPF Imaging не справился с файлом Беатрис — System.Drawing принудительно 
        // вычитает растр gdi-декодером ОС Windows и аппаратно пережмет его в sRGB!
        try
        {
            // Используем рефлексию или прямое обращение, если System.Drawing.Common подключен в проект
            using (var gdiImage = System.Drawing.Image.FromFile(rawCacheImagePath))
            {
                using (var bitmap = new System.Drawing.Bitmap(gdiImage.Width, gdiImage.Height))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

                        // Перерисовываем картинку на чистый sRGB холст
                        graphics.DrawImage(gdiImage, 0, 0, gdiImage.Width, gdiImage.Height);
                    }

                    // Сохраняем в канонический формат кодека JPEG
                    bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
            }

            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[GDI+ Триумф]: Проблемная картинка Беатрис {objectId} успешно перерисована в sRGB JPEG!");
                return targetPath;
            }
        }
        catch (Exception criticalEx)
        {
            System.Diagnostics.Debug.WriteLine($"[КРИТИЧЕСКИЙ СБОЙ ГРАФИКИ]: Не удалось распаковать файл {objectId}: {criticalEx.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// Извлекает (скачивает из облака или распаковывает из архива) бинарный поток 
    /// конкретного изображения и возвращает абсолютный путь к физическому файлу кэша.
    /// </summary>
    public abstract string ExtractImageToCache(string objectId);
}
