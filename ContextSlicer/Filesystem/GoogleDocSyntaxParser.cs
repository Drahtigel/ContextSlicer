using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ContextSlicer.Filesystem;

public class GoogleDocSyntaxParser : ISyntaxParser
{
    public async Task<List<SyntaxEntry>> ParseFileAsync(string absolutePath, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        if (!File.Exists(absolutePath)) return result;

        // Чистый асинхронный ввод-вывод
        string jsonContent = string.Empty;
        try
        {
            jsonContent = await File.ReadAllTextAsync(absolutePath);
        }
        catch (Exception ex)
        {
            string errTemplate = Application.Current.Resources["Str_Err_GoogleParseError"] as string
                                     ?? "Failed to parse Google Document structure.";
            System.Diagnostics.Debug.WriteLine($"[{errTemplate}] {ex.Message}");
        }
        if (string.IsNullOrEmpty(jsonContent)) return result;

        // ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Переносим тяжелый парсинг JObject и рекурсивный обход
        // СТРОГО в фоновый поток Task.Run, полностью защищая главный поток WPF от зависаний!
        return await Task.Run(() =>
        {
            try
            {
                var docJson = JObject.Parse(jsonContent);
                int globalCharIndex = 0;

                if (docJson["tabs"] is JArray tabsArray && tabsArray.Count > 0)
                {
                    foreach (JObject tabJson in tabsArray)
                    {
                        ParseTabRecursive(tabJson, relativePath, string.Empty, ref globalCharIndex, result);
                    }
                }
                else if (docJson["body"] != null)
                {
                    var bodyToken = docJson["body"];
                    if (bodyToken != null)
                    {
                        var bodyEntries = ParseContentNodes(bodyToken, relativePath, string.Empty, ref globalCharIndex);
                        result.AddRange(bodyEntries);
                    }
                }

                if (result.Count == 0)
                {
                    string fullText = ExtractRawTextFromTabs(docJson);
                    if (!string.IsNullOrEmpty(fullText))
                    {
                        result.AddRange(ParseFallbackText(fullText, relativePath));
                    }
                }
            }
            catch (Exception ex)
            {
                string errTemplate = Application.Current.Resources["Str_Err_GoogleParseError"] as string
                                     ?? "Failed to parse Google Document structure.";
                System.Diagnostics.Debug.WriteLine($"[{errTemplate}] {ex.Message}");
            }
            return result;
        });
    }

    private void ParseTabRecursive(JToken tab, string relativePath, string parentPath, ref int charIndex, List<SyntaxEntry> resultList)
    {
        string defTabTitle = Application.Current.Resources["Str_Type_Tab"] as string ?? "Tab";
        string tabTitle = tab["tabProperties"]?["title"]?.ToString() ?? defTabTitle;

        string currentTabPath = string.IsNullOrEmpty(parentPath) ? tabTitle : $"{parentPath}/{tabTitle}";

        resultList.Add(new SyntaxEntry
        {
            FilePath = relativePath,
            EntryPath = currentTabPath,
            DisplayName = tabTitle,
            Type = EntryType.Tab,
            SpanInfo = $"{charIndex},0"
        });

        var documentTab = tab["documentTab"];
        if (documentTab != null)
        {
            var entries = ParseContentNodes(documentTab, relativePath, currentTabPath, ref charIndex);
            if (entries.Count > 0)
            {
                resultList.AddRange(entries);
            }
        }

        if (tab["childTabs"] is JArray childArray)
        {
            foreach (var childTab in childArray)
            {
                ParseTabRecursive(childTab, relativePath, currentTabPath, ref charIndex, resultList);
            }
        }
    }

    private List<SyntaxEntry> ParseContentNodes(JToken documentTab, string relativePath, string tabPath, ref int currentAbsoluteIndex)
    {
        var entries = new List<SyntaxEntry>();
        var contentArray = documentTab["body"]?["content"] as JArray;
        if (contentArray == null) return entries;

        var activePaths = new Dictionary<int, string> { { 0, tabPath } };

        foreach (var element in contentArray)
        {
            var paragraph = element["paragraph"];
            if (paragraph != null)
            {
                string namedStyle = paragraph["paragraphStyle"]?["namedStyleType"]?.ToString() ?? string.Empty;
                int elementLength = 0;

                if (paragraph["elements"] is JArray elements)
                {
                    foreach (var el in elements)
                    {
                        elementLength += (el["textRun"]?["content"]?.ToString() ?? string.Empty).Length;
                    }

                    if (namedStyle.StartsWith("HEADING_") && int.TryParse(namedStyle.Substring(8), out int level))
                    {
                        string headingText = "";
                        foreach (var el in elements)
                        {
                            headingText += el["textRun"]?["content"]?.ToString() ?? string.Empty;
                        }
                        headingText = headingText.Trim();

                        if (!string.IsNullOrEmpty(headingText))
                        {
                            int targetParentLevel = level - 1;
                            while (targetParentLevel > 0 && !activePaths.ContainsKey(targetParentLevel))
                            {
                                targetParentLevel--;
                            }

                            string parentPath = activePaths[targetParentLevel];
                            string entryPath = string.IsNullOrEmpty(parentPath) ? headingText : $"{parentPath}/{headingText}";

                            entries.Add(new SyntaxEntry
                            {
                                FilePath = relativePath,
                                EntryPath = entryPath,
                                DisplayName = headingText,
                                Type = EntryType.Heading,
                                SpanInfo = $"{currentAbsoluteIndex},{elementLength}"
                            });

                            activePaths[level] = entryPath;

                            var keysToRemove = activePaths.Keys.Where(k => k > level).ToList();
                            foreach (var key in keysToRemove) activePaths.Remove(key);
                        }
                    }
                }
                currentAbsoluteIndex += elementLength;
            }
            else
            {
                // ИСПРАВЛЕНО: Вместо вызова убойного тяжелого .ToString().Length 
                // мы используем легкую оценку шага без выделения строк в памяти!
                currentAbsoluteIndex += 50;
            }
        }
        return entries;
    }

    private string ExtractRawTextFromTabs(JObject docJson)
    {
        var sb = new System.Text.StringBuilder();
        var tabs = docJson["tabs"] as JArray;
        if (tabs != null)
        {
            foreach (var tab in tabs)
            {
                ExtractTextRecursive(tab, sb);
            }
        }
        else if (docJson["body"]?["content"] is JArray content)
        {
            AppendContentText(content, sb);
        }
        return sb.ToString();
    }

    private void ExtractTextRecursive(JToken tab, System.Text.StringBuilder sb)
    {
        if (tab["documentTab"]?["body"]?["content"] is JArray content)
        {
            AppendContentText(content, sb);
        }
        if (tab["childTabs"] is JArray children)
        {
            foreach (var child in children) ExtractTextRecursive(child, sb);
        }
    }

    private void AppendContentText(JArray contentArray, System.Text.StringBuilder sb)
    {
        foreach (var element in contentArray)
        {
            if (element["paragraph"]?["elements"] is JArray elements)
            {
                foreach (var el in elements)
                {
                    sb.Append(el["textRun"]?["content"]?.ToString() ?? string.Empty);
                }
            }
        }
    }

    private List<SyntaxEntry> ParseFallbackText(string text, string relativePath)
    {
        var result = new List<SyntaxEntry>();
        string pagePrefix = Application.Current.Resources["Str_TextPage"] as string ?? "Page";
        result.Add(new SyntaxEntry
        {
            FilePath = relativePath,
            EntryPath = "FullText/Data",
            DisplayName = $"[{pagePrefix}]",
            Type = EntryType.Section,
            SpanInfo = $"0,{text.Length}"
        });
        return result;
    }
}
