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
    public virtual async Task GenerateTxtAsync(
        string outputPath, string fileName,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(outputPath) || string.IsNullOrWhiteSpace(fileName)) return;

        // Вызываем перегруженный метод наследника для формирования буфера
        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);

        // ТВОЕ ЖЕСТКОЕ ПРАВИЛО: Валидация буфера на пустоту перед записью
        if (sb == null || sb.Length == 0 || string.IsNullOrWhiteSpace(sb.ToString().Replace("=== ПРАВИЛА ОБРАЩЕНИЯ С ТЕКСТОМ И КОДОМ ===", "").Trim()))
        {
            throw new InvalidOperationException("Сгенерированный текстовый контекст пуст! Проверьте выбор элементов.");
        }

        string extension = fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "" : ".txt";
        string fullOutputPath = Path.Combine(outputPath, fileName + extension);

        await File.WriteAllTextAsync(fullOutputPath, sb.ToString(), Encoding.UTF8, token);

        // Системная подсветка файла в Проводнике Windows (строго один раз!)
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
    /// Упреждающий задел на будущее: контракт на обратную сборку структурированного .docx файла.
    /// </summary>
    public abstract Task GenerateDocxAsync(
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
} // Финальное закрытие абстрактного класса ContextBuilderBase!
