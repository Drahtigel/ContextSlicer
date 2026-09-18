using System;
using System.Collections.Generic;

namespace ContextSlicer
{
    // Строгое перечисление типов поддерживаемых источников проекта
    public enum ProjectType
    {
        Folder = 0,
        GoogleDoc = 1,
        WordDoc = 2
    }

    public class ContextModule
    {
        public string ModuleName { get; set; } = string.Empty;
        public string ContextFileName { get; set; } = string.Empty;
        public string ModuleRules { get; set; } = string.Empty;
        public List<string> CheckedFiles { get; set; } = new List<string>();
        public List<SyntaxEntry> CheckedEntries { get; set; } = new List<SyntaxEntry>();
    }

    public class ProjectConfig
    {
        public string ProjectName { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty; // Папка, URL или путь к .docx
        public string OutputPath { get; set; } = string.Empty;
        public string PromptRules { get; set; } = string.Empty;
        public bool IncludeDirectoryStructure { get; set; } = true;

        // ОБНОВЛЕНО: Вместо bool IsUrl теперь используем перечисление типов
        public ProjectType Type { get; set; } = ProjectType.Folder;

        public List<ContextModule> Modules { get; set; } = new List<ContextModule>();

        // ОБНОВЛЕНО: Интеллектуальный вывод иконок для ListBox на основе типа проекта
        [Newtonsoft.Json.JsonIgnore]
        public string ProjectTypeIcon => Type switch
        {
            ProjectType.GoogleDoc => "🌐",
            ProjectType.WordDoc => "📝",
            _ => "📁"
        };
    }
}
