using System.Collections.Generic;

namespace ContextSlicer
{
    // Класс для конкретного нарезанного модуля
    // Класс для конкретного нарезанного модуля
    public class ContextModule
    {
        public string ModuleName { get; set; } = string.Empty;
        public string ContextFileName { get; set; } = string.Empty;
        // Текстовые правила конкретно для этого модуля
        public string ModuleRules { get; set; } = string.Empty;
        // Список относительных путей файлов, отмеченных ИМЕННО ДЛЯ ЭТОГО модуля
        public List<string> CheckedFiles { get; set; } = new List<string>();
    }


    // Класс самого проекта (корневой папки)
    public class ProjectConfig
    {
        public string ProjectName { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string PromptRules { get; set; } = string.Empty;
        public bool IncludeDirectoryStructure { get; set; } = true;
        // Список всех нарезанных модулей внутри проекта
        public List<ContextModule> Modules { get; set; } = new List<ContextModule>();
    }
}
