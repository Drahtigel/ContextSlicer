using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Абстрактный монолитный фундамент для всех сервисов сборки и физической выгрузки контекста ИИ.
/// </summary>
public abstract class ContextBuilderBase
{
    protected readonly string PromptRules;
    protected readonly string ModuleRules;
    protected readonly bool IncludeDirectoryStructure;
    protected readonly bool IncludeImages;

    protected ContextBuilderBase(string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages = false)
    {
        PromptRules = promptRules ?? string.Empty;
        ModuleRules = moduleRules ?? string.Empty;
        IncludeDirectoryStructure = includeDirectoryStructure;
        IncludeImages = includeImages;
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: ЧИСТАЯ ФИЛЬТРАЦИЯ ТЕГОВ КАРТИНОК ИЗ ТЕКСТОВОГО КОНТЕКСТА-->
    // ================================================================= -->
    /// <summary>
    /// Метод постобработки текстового буфера. Если в дочернем классе не переопределено,
    /// полностью вычищает технические теги изображений для сохранения чистоты ИИ-контекста.
    /// </summary>
    protected string CleanTextFromImageTags(string rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return string.Empty;

        // Регулярное выражение находит любые инлайновые XML-теги картинок вида <image ... />
        return System.Text.RegularExpressions.Regex.Replace(
            rawText,
            @"<image\s+[^>]*/>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Абстрактный метод извлечения текстового контента, уникальный для каждого типа проекта.
    /// </summary>
    public abstract Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token);

    /// <summary>
    /// Унифицированный шаблонный метод генерации финального TXT-контекста на диск.
    /// Железно одинаков для кода, Word и Google Docs!
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: ФИЛЬТРАЦИЯ МАРКЕРОВ ИЗОБРАЖЕНИЙ ПЕРЕД ЗАПИСЬЮ В TXT    -->
    // ================================================================= -->
    public virtual async Task GenerateTxtAsync(
        string outputPath, string fileName,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(fileName)) return;

        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);

        // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Текстовый сборщик должен в принципе убирать этот тег!
        // Пропускаем буфер через CleanTextFromImageTags, полностью очищая контекст ИИ от тегов картинок
        string finalCleanText = CleanTextFromImageTags(sb?.ToString() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(finalCleanText) || string.IsNullOrWhiteSpace(finalCleanText.Replace("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===", "").Trim()))
        {
            throw new InvalidOperationException("Сгенерированный текстовый контекст пуст! Проверьте выбор элементов.");
        }

        string extension = fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "" : ".txt";
        string fullOutputPath = Path.Combine(outputPath, fileName + extension);

        await File.WriteAllTextAsync(fullOutputPath, finalCleanText, Encoding.UTF8, token);

        if (File.Exists(fullOutputPath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullOutputPath}\"");
        }
    }

    /// <summary>
    /// Контракт на генерацию PDF (будет реализован в базовом классе во 2-й части).
    /// </summary>
    public abstract Task GeneratePdfAsync(
        string outputPath, string fileName,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        CancellationToken token);


    /// <summary>
    /// Выгружает заголовки системных промптов и правил модуля в итоговый буфер.
    /// </summary>
    protected void AppendSystemRules(StringBuilder sb)
    {
        if (!string.IsNullOrWhiteSpace(PromptRules) || !string.IsNullOrWhiteSpace(ModuleRules))
        {
            sb.AppendLine("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===");
            if (!string.IsNullOrWhiteSpace(PromptRules))
                sb.AppendLine("<project_rules>" + PromptRules.Trim() + "</project_rules>\n");
            if (!string.IsNullOrWhiteSpace(ModuleRules))
                sb.AppendLine("<module_rules>" + ModuleRules.Trim() + "</module_rules>\n");
        }
    }

    /// <summary>
    /// Универсальный гвардейский валидатор каскада путей для литературных проектов.
    /// Гарантирует, что дочерний элемент не попадет в ИИ, если выключен его родитель.
    /// </summary>
    protected bool IsPathChainValid(string currentPath, HashSet<string> selectedPathsSet)
    {
        if (string.IsNullOrEmpty(currentPath) || selectedPathsSet == null) return false;

        string[] parts = currentPath.Split('/');
        string accumulatedPath = string.Empty;

        for (int i = 0; i < parts.Length; i++)
        {
            accumulatedPath = i == 0 ? parts[i] : $"{accumulatedPath}/{parts[i]}";

            if (!selectedPathsSet.Contains(accumulatedPath))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Утилита для выставления стандартных издательских полей страниц в MigraDoc (2 см со всех сторон).
    /// </summary>
    protected void SetSectionMargins(MigraDoc.DocumentObjectModel.Section section)
    {
        if (section == null) return;
        section.PageSetup.TopMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2);
    }

    /// <summary>
    /// Физически сохраняет скомпилированный PDF на жесткий диск и подсвечивает его в Проводнике Windows.
    /// </summary>
    protected void SavePdfToDiskAndOpen(MigraDoc.DocumentObjectModel.Document document, string outputPath, string fileName)
    {
        if (document == null || string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(fileName)) return;

        var renderer = new MigraDoc.Rendering.PdfDocumentRenderer();
        renderer.Document = document;
        renderer.RenderDocument();

        string cleanFileName = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;

        string baseFullPath = Path.Combine(outputPath, cleanFileName + ".pdf");
        string finalFullPath = baseFullPath;

        if (File.Exists(baseFullPath))
        {
            try
            {
                File.Delete(baseFullPath);
            }
            catch (IOException)
            {
                string timeSuffix = DateTime.Now.ToString("HHmmss");
                finalFullPath = Path.Combine(outputPath, $"{cleanFileName}_{timeSuffix}.pdf");
            }
        }

        try
        {
            renderer.PdfDocument.Save(finalFullPath);

            if (File.Exists(finalFullPath))
            {
                string argument = $"/select,\"{finalFullPath}\"";
                System.Diagnostics.Process.Start("explorer.exe", argument);
            }
        }
        catch (Exception ex)
        {
            string errTitle = System.Windows.Application.Current.Resources["Str_Err_GeneralErrorTitle"] as string ?? "Ошибка";
            string errTemplate = System.Windows.Application.Current.Resources["Str_Err_PdfSaveFailed"] as string ?? "Не удалось сохранить PDF документ:";
            System.Windows.MessageBox.Show($"{errTemplate} {ex.Message}", errTitle, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
    // ================================================================= -->
    // ДОБАВЛЕНО: АВТОНОМНЫЙ ПОЛИМОРФНЫЙ ПОДСЧЕТ ВЕСА ДЛЯ ИНТЕРФЕЙСА    -->
    // ================================================================= -->
    /// <summary>
    /// Виртуальный метод подсчета веса контекста. Сборщики сами собирают текст 
    /// и отдают наружу только чистые цифры, полностью разгружая ViewModel!
    /// </summary>
    public virtual async Task<(int Characters, int Tokens)> CalculateSizeAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        // Базовая реализация генерирует текст штатными средствами наследника
        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);
        string text = sb.ToString();

        int chars = text.Length;
        int tokens = (int)Math.Ceiling(chars / 4.0); // Единый базовый ИИ-коэффициент

        return (chars, tokens);
    }
    /// <summary>
    /// Абстрактный контракт на генерацию файла контекста в формате MS Word.
    /// Помечен как virtual для специфического переопределения во всех трех сборщиках!
    /// </summary>
    public virtual Task GenerateDocxAsync(
        string outputPath,
        string fileName,
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        return Task.CompletedTask;
    }

} // Финальное закрытие абстрактного класса ContextBuilderBase!
