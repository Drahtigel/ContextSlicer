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

// ================================================================= -->
// ИСПРАВЛЕНО: ОБЪЕКТНЫЙ САМОДОСТАТОЧНЫЙ DOM-УЗЕЛ DOCENTRY ДЛЯ ИИ   -->
// ================================================================= -->
public class DocEntry
{
    public string EntryTitle { get; set; } = string.Empty;
    public string EntryContent { get; set; } = string.Empty; // Плоский текст (если есть)
    public string EntryPath { get; set; } = string.Empty;
    public DocEntryType Type { get; set; }
    public int HeadingLevel { get; set; } = 0;

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
                unchecked { foreach (char c in safePath) hash = (hash * 31) + c; }
                _generatedId = $"{prefix}_{Math.Abs(hash).ToString("x4")}";
            }
            return _generatedId;
        }
    }

    /// <summary>
    /// ТВОЙ КОНЦЕПТ: Рекурсивно собирает контент узла и его детей, 
    /// самостоятельно заворачивая содержимое в валидные теги разметки ИИ.
    /// </summary>
    public string GetFormattedContent(bool includeImages)
    {
        var sb = new StringBuilder();
        string idAttr = this.GeneratedId;

        // 1. Обертка для контейнеров (Вкладки и Заголовки глав)
        if (Type == DocEntryType.Tab || Type == DocEntryType.Heading)
        {
            string tagName = Type == DocEntryType.Tab ? "tab" : "heading";
            sb.AppendLine($"<{tagName} id=\"{idAttr}\" path=\"{EntryPath}\">");

            // Наливаем собственный текст узла, если он есть
            if (!string.IsNullOrWhiteSpace(EntryContent))
            {
                sb.AppendLine(EntryContent.Trim());
            }

            // Рекурсивно собираем контент всех дочерних элементов матрёшки
            foreach (var child in ChildEntries)
            {
                string childContent = child.GetFormattedContent(includeImages);
                if (!string.IsNullOrWhiteSpace(childContent))
                {
                    sb.Append(childContent);
                }
            }

            sb.AppendLine($"</{tagName} id=\"{idAttr}\">");
        }
        // 2. Обертка для нативных списков <ul> / <li>
        else if (Type == DocEntryType.ListContainer)
        {
            sb.AppendLine($"<ul id=\"{idAttr}\">");
            foreach (var child in ChildEntries) sb.Append(child.GetFormattedContent(includeImages));
            sb.AppendLine($"</ul id=\"{idAttr}\">");
        }
        else if (Type == DocEntryType.ListItem)
        {
            sb.AppendLine($"  <li level=\"{ListNestingLevel}\">{EntryContent.Trim()}</li>");
        }
        // 3. Обертка для нативных таблиц <table>
        else if (Type == DocEntryType.Table)
        {
            sb.AppendLine("  <table>");
            foreach (var row in ChildEntries) sb.Append(row.GetFormattedContent(includeImages));
            sb.AppendLine("  </table>");
        }
        else if (Type == DocEntryType.TableRow)
        {
            sb.AppendLine("    <tr>");
            foreach (var cell in ChildEntries) sb.Append(cell.GetFormattedContent(includeImages));
            sb.AppendLine("    </tr>");
        }
        else if (Type == DocEntryType.TableCell)
        {
            sb.AppendLine($"      <td>{EntryContent.Trim()}</td>");
        }
        // 4. Обертка для изолированных изображений <image />
        else if (Type == DocEntryType.Image)
        {
            if (includeImages)
            {
                sb.AppendLine($"<image src=\"{ImageSourceId}_converted.jpg\" alt=\"{EntryTitle}\" />");
            }
        }
        // 5. Обычный художественный абзац прозы
        else if (Type == DocEntryType.Paragraph)
        {
            if (!string.IsNullOrWhiteSpace(EntryContent))
            {
                sb.AppendLine(EntryContent.Trim());
            }
        }

        return sb.ToString();
    }

    // ================================================================= -->
    // ДОБАВЛЕНО: КЛОНИРОВАНИЕ И ФИЛЬТРАЦИЯ DOM-ВЕТВЕЙ ДЛЯ СБОРЩИКА      -->
    // ================================================================= -->
    /// <summary>
    /// ТВОЙ КОНЦЕПТ: Проверяет ветку и возвращает её отфильтрованный слепок.
    /// Если узел одобрен на UI или имеет одобренных детей, он попадает в слепок,
    /// полностью сохраняя иерархию папок-транзитов и контент!
    /// </summary>
    public DocEntry? CloneApprovedBranch(HashSet<string> approvedPaths)
    {
        if (approvedPaths == null) return null;

        // Проверяем, выбрана ли данная конкретная нода на интерфейсе
        bool isCurrentApproved = approvedPaths.Contains(this.EntryPath);

        // Рекурсивно собираем одобренных детей
        var filteredChildren = new List<DocEntry>();
        foreach (var child in this.ChildEntries)
        {
            var approvedChild = child.CloneApprovedBranch(approvedPaths);
            if (approvedChild != null)
            {
                filteredChildren.Add(approvedChild);
            }
        }

        // Ловить нечего: ни сам узел не выбран, ни детей внутри этой ветки нет!
        if (!isCurrentApproved && filteredChildren.Count == 0)
        {
            return null;
        }

        // Создаем чистый поверхностный клон текущего узла структуры
        var clone = new DocEntry
        {
            EntryTitle = this.EntryTitle,
            EntryPath = this.EntryPath,
            Type = this.Type,
            HeadingLevel = this.HeadingLevel,
            ImageSourceId = this.ImageSourceId,
            ListNestingLevel = this.ListNestingLevel,
            TableMatrix = this.TableMatrix,
            // Если галочка на сам узел не стоит (это транзитная папка), 
            // его собственный контент обнуляем, чтобы не было дубликатов текста!
            EntryContent = isCurrentApproved ? this.EntryContent : string.Empty
        };

        // Навешиваем отфильтрованных детей и восстанавливаем ссылки на родителя
        foreach (var childClone in filteredChildren)
        {
            childClone.Parent = clone;
            clone.ChildEntries.Add(childClone);
        }

        return clone;
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

    // ================================================================= -->
    // ДОБАВЛЕНО: ЕДИНЫЙ ЦЕНТРАЛИЗОВАННЫЙ СБОРЩИК ОГЛАВЛЕНИЯ ИЗ DOM     -->
    // ================================================================= -->
    /// <summary>
    /// Универсальный метод базового класса для построения валидного 
    /// XML-блока оглавления структуры на основе живого объектного DOM-дерева.
    /// </summary>
    public string BuildUniversalStructureXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<document_structure>");
        sb.AppendLine("  <ul id=\"toc_root\">");

        // Запускаем рекурсивный обход корневых элементов дерева
        foreach (var child in this.ChildEntries)
        {
            BuildStructureLineRecursive(sb, child, 1);
        }

        sb.AppendLine("  </ul id=\"toc_root\">");
        sb.Append("</document_structure>");
        return sb.ToString();
    }

    /// <summary>
    /// Вспомогательный рекурсивный обходчик ветвей для выстраивания честных уровней вложенности.
    /// </summary>
    private static void BuildStructureLineRecursive(StringBuilder sb, DocEntry entry, int currentLevel)
    {
        if (entry == null) return;

        // Определяем строковый маркер типа узла для XML-разметки оглавления
        string typeStr = entry.Type switch
        {
            DocEntryType.Tab => "tab",
            DocEntryType.Title => "title",
            DocEntryType.Subtitle => "subtitle",
            _ => "heading"
        };

        // Заводим уникальный псевдо-ID на основе хэш-кода для валидности XML-атрибутов
        string idAttr = "t_" + Math.Abs(entry.EntryTitle.GetHashCode()).ToString("x8");

        // Генерируем снайперскую строчку элемента списка с точным runtime-уровнем level
        sb.AppendLine($"    <li level=\"{currentLevel}\" type=\"{typeStr}\" id=\"{idAttr}\">{entry.EntryTitle}</li>");

        // Уходим вглубь по дочерней коллекции матрёшки, честно инкрементируя шаг уровня
        foreach (var child in entry.ChildEntries)
        {
            BuildStructureLineRecursive(sb, child, currentLevel + 1);
        }
    }

}
