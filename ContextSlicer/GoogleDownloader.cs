using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ContextSlicer;

public class GoogleDownloader
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private const string CacheFolderName = "googlecache";

    // Метод извлечения ID документа из любых вариантов ссылок Google Docs
    public static string ExtractDocumentId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var match = Regex.Match(url, @"/document/d/([a-zA-Z0-9-_]+)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // Скачивание и сохранение структуры документа в локальный JSON-кэш
    public async Task<bool> DownloadToCacheAsync(string url, string projectLocation)
    {
        string docId = ExtractDocumentId(url);
        if (string.IsNullOrEmpty(docId)) return false;

        // Формируем путь к папке кэша внутри директории хранения проектов
        string cacheDir = Path.Combine(projectLocation, CacheFolderName);
        if (!Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }

        string cacheFilePath = Path.Combine(cacheDir, $"{docId}.json");

        try
        {
            // На первом этапе мы работаем по открытой ссылке. 
            // Google Docs API позволяет выгрузить документ в формате JSON через специальную публичную точку экспорта,
            // либо мы используем классический endpoint Docs API с ключом.
            // Формируем URL для вычитки структуры документа
            string requestUrl = $"https://google.com{docId}/export?format=txt";
            // Внимание: Полную структуру JSON со стилями и вкладками выдает Docs API:
            // https://googleapis.com{docId}?key=YOUR_API_KEY

            // Для теста доступности и вычитки отправляем запрос:
            var response = await _httpClient.GetAsync($"https://google.com{docId}/edit");
            if (!response.IsSuccessStatusCode) return false;

            // Временно создаем заглушку структуры JSON для интеграции, пока не подключим ключи
            JObject docStructure = new JObject
            {
                ["title"] = "Google Document",
                ["documentId"] = docId,
                ["body"] = new JObject { ["content"] = new JArray() }
            };

            // Записываем структурированный JSON в googlecache
            await File.WriteAllTextAsync(cacheFilePath, docStructure.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
