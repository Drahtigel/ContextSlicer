using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace ContextSlicer;

public class GlobalFilters
{
    public ObservableCollection<string> ExcludedFolders { get; set; } = new();
    public ObservableCollection<string> ExcludedExtensions { get; set; } = new();
}

public static class GlobalFilterService
{
    private static readonly string FilePath;
    public static GlobalFilters Current { get; private set; } = new();

    static GlobalFilterService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "ContextSlicer");
        Directory.CreateDirectory(appDir);
        FilePath = Path.Combine(appDir, "global_filters.json");

        LoadFilters();
    }

    public static void LoadFilters()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<GlobalFilters>(json);
                if (loaded != null)
                {
                    Current = loaded;
                    return;
                }
            }
        }
        catch { }

        // Если файла нет или он поврежден — накатываем базовый дефолтный список
        Current = new GlobalFilters
        {
            ExcludedFolders = new ObservableCollection<string> { "bin", "obj", ".vs", "publish", ".git", ".idea", "node_modules" },
            ExcludedExtensions = new ObservableCollection<string> { ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".mp3", ".wav", ".zip", ".rar", ".dll", ".exe", ".pdb", ".suo", ".user", ".bak", ".tmp", ".log" }
        };
        SaveFilters();
    }

    public static void SaveFilters()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    // Теперь проверки папок и расширений работают на основе глобальных ObservableCollection динамически
    public static bool IsFolderExcluded(string folderName)
    {
        foreach (var f in GlobalFilterService.Current.ExcludedFolders)
        {
            if (folderName.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsExtensionExcluded(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        foreach (var e in GlobalFilterService.Current.ExcludedExtensions)
        {
            if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

}
