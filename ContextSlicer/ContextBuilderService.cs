using ContextSlicer.ContextBuilder;
using ContextSlicer.Filesystem;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ContextSlicer;

public struct ProgressReport
{
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public string CurrentFileName { get; set; }
}

/// <summary>
/// Легковесная Фабрика-Диспетчер. Полностью сохраняет обратную совместимость с ViewModel,
/// но делегирует сборку и генерацию контекстов специализированным ООП-стратегиям.
/// </summary>
public static class ContextBuilderService
{
    private static ContextBuilderBase CreateBuilder(ProjectType type, string promptRules, string moduleRules, bool includeDirectoryStructure, bool includeImages)
    {
        return type switch
        {
            ProjectType.GoogleDoc => new ContextBuilderGoogle(promptRules, moduleRules, includeDirectoryStructure, includeImages),
            ProjectType.WordDoc => new ContextBuilderWord(promptRules, moduleRules, includeDirectoryStructure, includeImages),
            _ => new ContextBuilderCode(promptRules, moduleRules, includeDirectoryStructure)
        };
    }

    public static async Task<StringBuilder> BuildTextContentAsync(
        string promptRules, string moduleRules, bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progressHandler, CancellationToken token)
    {
        return await BuildTextContentAsync(promptRules, moduleRules, includeDirectoryStructure,
            checkedFiles, savedEntries, progressHandler, ProjectType.Folder, false, token);
    }

    public static async Task<StringBuilder> BuildTextContentAsync(
        string promptRules, string moduleRules, bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progressHandler, ProjectType projectType,
        bool includeImages, CancellationToken token)
    {
        var builder = CreateBuilder(projectType, promptRules, moduleRules, includeDirectoryStructure, includeImages);
        return await builder.BuildTextContentAsync(checkedFiles, savedEntries, token);
    }

    public static async Task GenerateContextFileAsync(
        string outputPath, string fileName, string projectRules, string moduleRules,
        bool includeDirectoryStructure, FileSystemNode rootNode,
        List<SyntaxEntry> savedEntries, IProgress<ProgressReport> progress,
        ProjectType projectType, CancellationToken token)
    {
        var checkedFiles = new List<FileSystemNode>();
        GetCheckedFilesExtended(rootNode, checkedFiles);

        bool isLiterary = projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc;
        if (isLiterary && rootNode != null && !checkedFiles.Contains(rootNode))
        {
            checkedFiles.Add(rootNode);
        }

        var builder = CreateBuilder(projectType, projectRules, moduleRules, includeDirectoryStructure, false);
        await builder.GenerateTxtAsync(outputPath, fileName, checkedFiles, savedEntries, token);
    }

    public static async Task GeneratePdfContextFileAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, IProgress<ProgressReport> progressHandler,
        CancellationToken token)
    {
        await GeneratePdfContextFileAsync(outputPath, fileName, promptRules, moduleRules,
            includeDirectoryStructure, false, rootNode, checkedEntries, progressHandler, ProjectType.Folder, token);
    }

    public static async Task GeneratePdfContextFileAsync(
        string outputPath, string fileName, string promptRules, string moduleRules,
        bool includeDirectoryStructure, bool includeImages, FileSystemNode rootNode,
        List<SyntaxEntry> checkedEntries, IProgress<ProgressReport> progressHandler,
        ProjectType projectType, CancellationToken token)
    {
        var checkedFiles = new List<FileSystemNode>();
        GetCheckedFilesExtended(rootNode, checkedFiles);

        bool isLiterary = projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc;
        if (isLiterary && rootNode != null && !checkedFiles.Contains(rootNode))
        {
            checkedFiles.Add(rootNode);
        }

        var builder = CreateBuilder(projectType, promptRules, moduleRules, includeDirectoryStructure, includeImages);
        await builder.GeneratePdfAsync(outputPath, fileName, checkedFiles, checkedEntries, token);
    }

    public static void GetCheckedFiles(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null || result == null) return;
        if (node.IsFile && node.IsChecked == true)
        {
            if (!result.Contains(node)) result.Add(node);
        }
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            if (child != null) GetCheckedFiles(child, result);
        }
    }

    public static void GetCheckedFilesExtended(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null || result == null) return;
        if (node.IsFile && (node.IsChecked == true || node.IsChecked == null))
        {
            if (!result.Contains(node)) result.Add(node);
        }
        var childrenCopy = new List<FileSystemNode>(node.Children);
        foreach (var child in childrenCopy)
        {
            if (child != null) GetCheckedFilesExtended(child, result);
        }
    }

    // ================================================================= -->
    // ДОБАВЛЕНО: СЕРВИСНЫЙ МОСТ ДЛЯ ВЫЗОВА АВТОНОМНОГО ПОДСЧЕТА WEIGHT   -->
    // ================================================================= -->
    public static async Task<(int Characters, int Tokens)> GetContextSizeAsync(
        ProjectType projectType, bool includeDirectoryStructure,
        List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        // Создаем нужный билдер налету и просим его посчитать вес самостоятельно!
        var builder = CreateBuilder(projectType, string.Empty, string.Empty, includeDirectoryStructure, false);
        return await builder.CalculateSizeAsync(checkedFiles, savedEntries, token);
    }
    // ================================================================= -->
    // ДОБАВЛЕНО: СЕРВИСНЫЙ ДИСПЕТЧЕР ДЛЯ ПОДДЕРЖКИ ГЕНЕРАЦИИ EXPORT .DOCX -->
    // ================================================================= -->
    /// <summary>
    /// Централизованный статический метод генерации контекста в формате MS Word (.docx).
    /// Инициализирует нужный фабричный билдер и запускает OpenXML экспорт.
    /// </summary>
    public static async Task GenerateDocxContextFileAsync(
        string outputPath,
        string fileName,
        string promptRules,
        string moduleRules,
        bool includeDirectoryStructure,
        FileSystemNode rootNode,
        List<SyntaxEntry> savedEntries,
        IProgress<ProgressReport> progress,
        ProjectType projectType,
        CancellationToken token)
    {
        // 1. Вытаскиваем плоский список отмеченных пользователем на UI файлов диска
        var checkedFiles = new List<FileSystemNode>();
        GetCheckedFilesExtended(rootNode, checkedFiles);

        // Гарантируем проброс корня дерева для литературных документов прозы
        if ((projectType == ProjectType.GoogleDoc || projectType == ProjectType.WordDoc) && rootNode != null)
        {
            if (!checkedFiles.Contains(rootNode)) checkedFiles.Add(rootNode);
        }

        // 2. Создаем нужный билдер через фабрику стратегий
        var builder = CreateBuilder(projectType, promptRules, moduleRules, includeDirectoryStructure, false);
        if (builder == null) return;

        // 3. Запускаем асинхронную генерацию .docx документа силами выбранного класса
        await builder.GenerateDocxAsync(outputPath, fileName, checkedFiles, savedEntries, token);

        // 4. Одиночный автоматический запуск Проводника Windows для подсветки готового файла
        try
        {
            string fullPath = Path.Combine(outputPath, fileName);
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{fullPath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Explorer Open DOCX Error] {ex.Message}");
        }
    }

}
