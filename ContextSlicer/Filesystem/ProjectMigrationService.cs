using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ContextSlicer.Filesystem;

public static class ProjectMigrationService
{
    /// <summary>
    /// Выполняет одноразовый перенос проектов из старого монолитного projects.json в изолированные файлы папки projects
    /// </summary>
    public static void MigrateOldProjectsIfNeeded()
    {
        // ИСПРАВЛЕНО: Базовый путь перенесен строго в системную папку %AppData%/ContextSlicer
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "ContextSlicer");

        string oldJsonPath = Path.Combine(appDir, "projects.json");
        string markerPath = Path.Combine(appDir, "projects.migrated");
        string targetDir = Path.Combine(appDir, "projects");

        // Если маркер миграции уже существует или старого файла по правильному пути нет — выходим
        if (File.Exists(markerPath) || !File.Exists(oldJsonPath))
        {
            return;
        }

        try
        {
            // Гарантируем наличие изолированной директории проектов
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string oldContent = File.ReadAllText(oldJsonPath);
            if (string.IsNullOrWhiteSpace(oldContent)) return;

            // Парсим старую структуру
            JToken rootToken = JToken.Parse(oldContent);
            JArray? projectsArray = rootToken as JArray;

            if (projectsArray == null && rootToken["Projects"] is JArray arr)
            {
                projectsArray = arr;
            }

            if (projectsArray != null)
            {
                foreach (JObject projectObj in projectsArray)
                {
                    string projectName = projectObj["ProjectName"]?.ToString()
                        ?? projectObj["Name"]?.ToString()
                        ?? "UntitledProject";

                    // Вычищаем недопустимые символы файловой системы Windows
                    string safeName = string.Concat(projectName.Split(Path.GetInvalidFileNameChars())).Trim();
                    if (string.IsNullOrWhiteSpace(safeName)) safeName = "UntitledProject";

                    string targetFileName = $"{safeName}.json";
                    string targetFullPath = Path.Combine(targetDir, targetFileName);

                    // Защита от совпадения имен: добавляем временной индекс при конфликте
                    if (File.Exists(targetFullPath))
                    {
                        string timeSuffix = DateTime.Now.ToString("HHmmss");
                        targetFullPath = Path.Combine(targetDir, $"{safeName}_{timeSuffix}.json");
                    }

                    // Сохраняем изолированный проект
                    File.WriteAllText(targetFullPath, projectObj.ToString(Newtonsoft.Json.Formatting.Indented));
                }
            }

            // Создаем файл-пустышку, сигнализирующий о завершении нарезки базы
            File.WriteAllText(markerPath, $"Migrated successfully on {DateTime.Now}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Migration Critical Error] {ex.Message}");
        }
    }
}
