using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ContextSlicer.Filesystem;

namespace ContextSlicer.ContextBuilder;

/// <summary>
/// Изолированный сборщик контекста ИИ для технических проектов кода (C# / SQL).
/// Реализует хирургическое вырезание usings, классов и методов по координатам Roslyn.
/// </summary>
public class ContextBuilderCode : ContextBuilderBase
{
    public ContextBuilderCode(string promptRules, string moduleRules, bool includeDirectoryStructure)
        : base(promptRules, moduleRules, includeDirectoryStructure, false) { }

    public override async Task<StringBuilder> BuildTextContentAsync(
        List<FileSystemNode> checkedFiles,
        List<SyntaxEntry> savedEntries,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        if (checkedFiles == null || checkedFiles.Count == 0) return sb;

        // 1. Выгрузка системных правил промпта и модуля из абстрактного класса
        AppendSystemRules(sb);

        // 2. Генерация структуры выбранного кода (оглавления) строго без дубликатов
        if (IncludeDirectoryStructure)
        {
            sb.AppendLine("=== СТРУКТУРА ВЫБРАННОГО КОДА ===");
            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in checkedFiles)
            {
                if (file != null && !string.IsNullOrEmpty(file.RelativePath) && uniquePaths.Add(file.RelativePath))
                {
                    sb.AppendLine($"├─ {file.RelativePath}");
                }
            }
            sb.AppendLine();
        }

        // 3. Выгрузка содержимого файлов исходного кода
        for (int i = 0; i < checkedFiles.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var fileNode = checkedFiles[i];
            if (fileNode == null || !File.Exists(fileNode.FullPath)) continue;

            // БРОНЕБОЙНЫЙ ПЕРЕХВАТ: Ищем, выбраны ли конкретные методы (FUNC, PROP, SECT) внутри ноды этого файла.
            var selectedMethods = new List<FileSystemNode>();
            CollectCheckedLeavesOnly(fileNode, selectedMethods);

            // Если файл отмечен галочкой True (выбран целиком) ИЛИ внутри него не выбрано ни одной точечной функции
            bool isFullFileMode = (fileNode.IsChecked == true) || (selectedMethods.Count == 0);

            sb.AppendLine($"---{fileNode.RelativePath}---");
            string fullCodeText = await File.ReadAllTextAsync(fileNode.FullPath, token);

            if (isFullFileMode)
            {
                // Наливаем файл целиком, если точечные функции не выбраны
                sb.AppendLine(fullCodeText);
            }
            else
            {
                // ХИРУРГИЧЕСКИЙ РЕЖИМ НАБЛЮДЕНИЯ: Вырезаем только usings и выбранные функции по SpanInfo!
                ExtractTargetCodeBlocks(sb, fileNode, selectedMethods, fullCodeText);
            }
            sb.AppendLine();
        }

        return sb;
    }
    /// <summary>
    /// Точечно вырезает блоки импортов и выбранные функции по координатам Roslyn.
    /// </summary>
    private void ExtractTargetCodeBlocks(StringBuilder sb, FileSystemNode fileNode, List<FileSystemNode> selectedMethods, string fullCodeText)
    {
        // А) Автоматически вырезаем блок импортов (using), если он присутствует в структуре файла
        FileSystemNode? usingsNode = null;
        foreach (var child in fileNode.Children)
        {
            if (child != null && child.IsSyntaxNode && child.SyntaxType == EntryType.Section && child.Name.Contains("using"))
            {
                usingsNode = child;
                break;
            }
        }

        if (usingsNode != null)
        {
            var span = GetSpanValues(usingsNode.SyntaxSpanInfo);
            if (span.Start + span.Length <= fullCodeText.Length)
            {
                sb.AppendLine(fullCodeText.Substring(span.Start, span.Length));
                sb.AppendLine();
            }
        }

        // Б) Вырезаем чистый код только тех функций, напротив которых стоит честная галочка [✓]
        foreach (var methodNode in selectedMethods)
        {
            if (methodNode == null || string.IsNullOrEmpty(methodNode.SyntaxSpanInfo)) continue;

            var span = GetSpanValues(methodNode.SyntaxSpanInfo);
            if (span.Start + span.Length <= fullCodeText.Length)
            {
                sb.AppendLine($"// [ВЫРЕЗАННЫЙ БЛОК: {methodNode.Name}]");
                sb.AppendLine(fullCodeText.Substring(span.Start, span.Length));
                sb.AppendLine();
            }
        }
    }

    /// <summary>
    /// Рекурсивно собирает только конечные выбранные функции (листья) внутри ноды файла.
    /// </summary>
    private void CollectCheckedLeavesOnly(FileSystemNode node, List<FileSystemNode> result)
    {
        if (node == null || result == null) return;

        if (node.IsSyntaxNode && node.IsChecked == true &&
            (node.SyntaxType == EntryType.Function || node.SyntaxType == EntryType.Property || node.SyntaxType == EntryType.Section))
        {
            if (!node.Name.Contains("using")) result.Add(node);
        }

        foreach (var child in node.Children)
        {
            CollectCheckedLeavesOnly(child, result);
        }
    }

    /// <summary>
    /// Безопасно парсит строку координат SpanInfo "start,length".
    /// </summary>
    private (int Start, int Length) GetSpanValues(string spanInfo)
    {
        if (string.IsNullOrEmpty(spanInfo)) return (0, 0);
        var parts = spanInfo.Split(',');

        int start = parts.Length > 0 && int.TryParse(parts[0], out int s) ? s : 0;
        int len = parts.Length > 1 && int.TryParse(parts[1], out int l) ? l : 0;

        return (start, len);
    }

    /// <summary>
    /// Высокопроизводительный технический PDF-рендерер для листингов кода C# и T-SQL.
    /// </summary>
    public override async Task GeneratePdfAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        var sb = await BuildTextContentAsync(checkedFiles, savedEntries, token);
        string metaDataText = sb.ToString();

        if (string.IsNullOrWhiteSpace(metaDataText)) return;

        var document = new MigraDoc.DocumentObjectModel.Document();

        // Создаем технический моноширинный стиль для кода во избежание сбоев верстки
        var codeStyle = document.Styles.AddStyle("CodeStyle", "Normal");
        if (codeStyle != null)
        {
            codeStyle.Font.Name = "Courier New";
            codeStyle.Font.Size = 8.5;
            codeStyle.ParagraphFormat.LineSpacing = 11;
            codeStyle.ParagraphFormat.KeepTogether = true;
        }

        var currentSection = document.AddSection();
        SetSectionMargins(currentSection);

        using (var reader = new StringReader(metaDataText))
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                token.ThrowIfCancellationRequested();
                string cleanLine = line.Replace("\t", "    ");

                var p = currentSection.AddParagraph();
                p.Style = "CodeStyle";
                p.AddFormattedText(cleanLine);

                if (string.IsNullOrWhiteSpace(cleanLine))
                {
                    p.Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromPoint(2);
                }
            }
        }

        SavePdfToDiskAndOpen(document, outputPath, fileName);
    }

    public override Task GenerateDocxAsync(string outputPath, string fileName, List<FileSystemNode> checkedFiles, List<SyntaxEntry> savedEntries, CancellationToken token)
    {
        return Task.CompletedTask; // Задел под будущую выгрузку структуры кода в docx
    }
} // Финальное закрытие класса!
