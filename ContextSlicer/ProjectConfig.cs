using ContextSlicer.Filesystem;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
        public string GoogleApiKey { get; set; } = string.Empty;
        public bool IncludeImages { get; set; } = false;


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
        /// <summary>
        /// СТАТИЧЕСКАЯ ФАБРИКА: Загружает изолированный конфигурационный файл проекта с жесткого диска.
        /// </summary>
        public static ProjectConfig? Load(string absoluteFilePath)
        {
            if (string.IsNullOrWhiteSpace(absoluteFilePath) || !File.Exists(absoluteFilePath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(absoluteFilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return null;

                var project = JsonConvert.DeserializeObject<ProjectConfig>(json);
                return project;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectConfig.Load Error] Не удалось прочитать файл {absoluteFilePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// МЕТОД ЭКЗЕМПЛЯРА: Автономно сохраняет текущее состояние проекта в персональный JSON-файл.
        /// </summary>
        public bool Save(string targetDirectoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(this.ProjectName)) return false;

                // Гарантируем наличие папки для сохранения
                if (!Directory.Exists(targetDirectoryPath))
                {
                    Directory.CreateDirectory(targetDirectoryPath);
                }

                // Формируем безопасное имя файла на основе очищенного имени проекта
                string safeName = string.Concat(this.ProjectName.Split(Path.GetInvalidFileNameChars())).Trim();
                if (string.IsNullOrWhiteSpace(safeName)) safeName = "UntitledProject";

                string finalFullPath = Path.Combine(targetDirectoryPath, $"{safeName}.json");

                // Сериализуем объект самого себя в красивый отформатированный JSON
                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(finalFullPath, json, Encoding.UTF8);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ProjectConfig.Save Error] Ошибка записи на диск: {ex.Message}");
                return false;
            }
        }
    }
}
