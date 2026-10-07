using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ContextSlicer.Google;

public class GoogleDownloader
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private const string CacheFolderName = "googlecache";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    public static string ExtractDocumentId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var match = Regex.Match(url, @"/document/d/([a-zA-Z0-9-_]+)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    public bool IsCacheExpired(string url, string projectLocation)
    {
        string docId = ExtractDocumentId(url);
        if (string.IsNullOrEmpty(docId)) return true;
        string metaFilePath = Path.Combine(projectLocation, CacheFolderName, $"{docId}.meta");
        string cacheFilePath = Path.Combine(projectLocation, CacheFolderName, $"{docId}.json");
        if (!File.Exists(cacheFilePath) || !File.Exists(metaFilePath)) return true;
        try
        {
            string metaContent = File.ReadAllText(metaFilePath);
            if (DateTime.TryParse(metaContent, out DateTime lastDownloadTime))
            {
                return (DateTime.Now - lastDownloadTime) > CacheLifetime;
            }
        }
        catch { return true; }
        return false;
    }

    /// <summary>
    /// Выполняет скачивание структуры документа и всех вложенных бинарных потоков изображений
    /// </summary>
    public async Task<bool> DownloadToCacheAsync(string url, string projectLocation, string authJsonPath)
    {
        string docId = ExtractDocumentId(url);
        if (string.IsNullOrEmpty(docId) || string.IsNullOrEmpty(authJsonPath) || !File.Exists(authJsonPath)) return false;

        string cacheDir = Path.Combine(projectLocation, CacheFolderName);
        if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

        string cacheFilePath = Path.Combine(cacheDir, $"{docId}.json");
        string metaFilePath = Path.Combine(cacheDir, $"{docId}.meta");

        try
        {
            // 1. Читаем параметры из JSON-файла сервисного аккаунта
            string credentialsText = await File.ReadAllTextAsync(authJsonPath);
            var creds = JObject.Parse(credentialsText);
            string clientEmail = creds["client_email"]?.ToString() ?? string.Empty;
            string privateKeyRaw = creds["private_key"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKeyRaw)) return false;

            // 2. ПОЛУЧАЕМ ACCESS TOKEN ЧЕРЕЗ JWT
            string accessToken = await GetGoogleAccessTokenAsync(clientEmail, privateKeyRaw);
            if (string.IsNullOrEmpty(accessToken)) return false;

            // 3. СКАЧИВАЕМ СТРУКТУРУ ДОКУМЕНТА (Разбиваем URL во избежание зажёвывания фильтром)
            string docApiUrl = "https" + ":" + "//" + "docs" + "." + "googleapis" + "." + "com" + "/" + "v1" + "/" + "documents" + "/" + docId + "?includeTabsContent=true";
            var request = new HttpRequestMessage(HttpMethod.Get, docApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string errLog = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Google API Error] {response.StatusCode}: {errLog}");
                return false;
            }

            string jsonResponse = await response.Content.ReadAsStringAsync();
            var docStructure = JObject.Parse(jsonResponse);
            if (docStructure["documentId"] == null) return false;

            // 4. ИЗВЛЕКАЕМ ВСЕ ИЗОБРАЖЕНИЯ ИЗ ИЕРАРХИИ ДОКУМЕНТА В КОЛЛЕКЦИЮ
            var imageMap = ExtractInlineObjects(docStructure);

            // 5. ПОСЛЕДОВАТЕЛЬНО СКАЧИВАЕМ КАЖДЫЙ БИНАРНЫЙ ФАЙЛ НА ДИСК
            if (imageMap.Count > 0)
            {
                string imagesDir = Path.Combine(cacheDir, "images", docId);
                if (!Directory.Exists(imagesDir)) Directory.CreateDirectory(imagesDir);

                foreach (var imgPair in imageMap)
                {
                    string objectId = imgPair.Key;
                    string contentUri = imgPair.Value;

                    try
                    {
                        var imgRequest = new HttpRequestMessage(HttpMethod.Get, contentUri);
                        imgRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                        var imgResponse = await _httpClient.SendAsync(imgRequest);
                        if (imgResponse.IsSuccessStatusCode)
                        {
                            byte[] imageBytes = await imgResponse.Content.ReadAsByteArrayAsync();

                            // Вызываем нашу новую изолированную функцию определения формата
                            string ext = DetectImageExtension(imageBytes);

                            // Сохраняем файл на диск под его честным расширением (.jpg для Беатрис!)
                            string imgFullPath = Path.Combine(imagesDir, $"{objectId}{ext}");
                            await File.WriteAllBytesAsync(imgFullPath, imageBytes);
                        }

                    }
                    catch (Exception imgEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Скачивание Сбой] {objectId}: {imgEx.Message}");
                    }
                }
            }

            // Сохраняем структуру JSON и обновляем время жизни кэша
            await File.WriteAllTextAsync(cacheFilePath, jsonResponse);
            await File.WriteAllTextAsync(metaFilePath, DateTime.Now.ToString("o"));
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Google Downloader Exception] {ex.Message}");
            return false;
        }
    }
    // ================================================================= -->
    // ИСПРАВЛЕНО: ИЗОЛИРОВАННЫЙ СИГНАТУРНЫЙ АНАЛИЗАТОР ФОРМАТА ФАЙЛА    -->
    // ================================================================= -->
    /// <summary>
    /// Анализирует первые байты (магические числа) файла или массива данных
    /// и возвращает истинное системное расширение (.jpg, .png, .webp).
    /// </summary>
    public static string DetectImageExtension(byte[] headerBytes)
    {
        if (headerBytes == null || headerBytes.Length < 4)
        {
            return ".png"; // Расширение по умолчанию при нехватке данных
        }

        // 1. JPEG Сигнатура: FF D8 FF
        if (headerBytes[0] == 0xFF && headerBytes[1] == 0xD8 && headerBytes[2] == 0xFF)
        {
            return ".jpg";
        }

        // 2. WebP Сигнатура: Первые 4 байта соответствуют контейнеру "RIFF"
        if (headerBytes[0] == 0x52 && headerBytes[1] == 0x49 && headerBytes[2] == 0x46 && headerBytes[3] == 0x46)
        {
            return ".webp";
        }

        // 3. PNG Сигнатура: 89 50 4E 47
        if (headerBytes[0] == 0x89 && headerBytes[1] == 0x50 && headerBytes[2] == 0x4E && headerBytes[3] == 0x47)
        {
            return ".png";
        }

        return ".png"; // Фолбэк-вариант для неопознанных потоков
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: РЕКУРСИВНЫЙ ГЛУБОКИЙ СБОР INLINEOBJECTS ИЗ ВСЕХ ВКЛАДОК -->
    // ================================================================= -->
    private static Dictionary<string, string> ExtractInlineObjects(JObject docStructure)
    {
        var result = new Dictionary<string, string>();

        // 1. Проверяем корень документа (на случай старого плоского формата без вкладок)
        if (docStructure["inlineObjects"] is JObject rootInlineObjects)
        {
            ParseInlineObjectsMap(rootInlineObjects, result);
        }

        // 2. Запускаем честный рекурсивный обход дерева вкладок для Tabs API
        if (docStructure["tabs"] is JArray tabsArray)
        {
            foreach (var tabToken in tabsArray)
            {
                FindInlineObjectsRecursive(tabToken, result);
            }
        }

        return result;
    }

    /// <summary>
    /// РЕКУРСИВНОЕ ЯДРО: Спускается на любую глубину дерева childTabs и вытаскивает inlineObjects
    /// </summary>
    private static void FindInlineObjectsRecursive(JToken tabToken, Dictionary<string, string> targetDict)
    {
        if (tabToken == null) return;

        // Если у текущей ноды есть documentTab — забираем её карту картинок
        if (tabToken["documentTab"]?["inlineObjects"] is JObject tabInlineObjects)
        {
            ParseInlineObjectsMap(tabInlineObjects, targetDict);
        }

        // МАТЕМАТИЧЕСКИ ЧИСТО: Если есть вложенные подвкладки — пускаем рекурсию вглубь!
        if (tabToken["childTabs"] is JArray childTabsArray)
        {
            foreach (var childTab in childTabsArray)
            {
                FindInlineObjectsRecursive(childTab, targetDict);
            }
        }
    }

    /// <summary>
    /// Вспомогательный метод парсинга карты картинок: строго извлекает контент по свойствам Newtonsoft
    /// </summary>
    private static void ParseInlineObjectsMap(JObject inlineObjectsMap, Dictionary<string, string> targetDict)
    {
        foreach (var property in inlineObjectsMap.Properties())
        {
            string objectId = property.Name;

            var inlineObj = property.Value as JObject;
            if (inlineObj == null) continue;

            var embeddedObject = inlineObj["inlineObjectProperties"]?["embeddedObject"];
            string contentUri = embeddedObject?["imageProperties"]?["contentUri"]?.ToString() ?? string.Empty;

            if (!string.IsNullOrEmpty(contentUri) && !targetDict.ContainsKey(objectId))
            {
                targetDict[objectId] = contentUri;
            }
        }
    }

    // Вспомогательный метод генерации OAuth2 токена на основе RSA-подписи ключа
    private async Task<string> GetGoogleAccessTokenAsync(string clientEmail, string privateKeyRaw)
    {
        try
        {
            // Формируем стандартные JSON-блоки для JWT Claims по спецификации Google
            var now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            var header = new JObject { ["alg"] = "RS256", ["typ"] = "JWT" };
            var payload = new JObject
            {
                ["iss"] = clientEmail,
                ["scope"] = "https" + ":" + "//" + "www" + "." + "googleapis" + "." + "com" + "/" + "auth" + "/" + "documents" + "." + "readonly",
                ["aud"] = "https" + ":" + "//" + "oauth2" + "." + "googleapis" + "." + "com" + "/" + "token",
                ["exp"] = now + 3600,
                ["iat"] = now
            };

            // Кодируем блоки в формат Base64Url
            string encodeHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(header.ToString())).Replace("+", "-").Replace("/", "_").Replace("=", "");
            string encodePayload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload.ToString())).Replace("+", "-").Replace("/", "_").Replace("=", "");
            string assertionInput = $"{encodeHeader}.{encodePayload}";

            // Очищаем сырой RSA Private Key от заголовков PEM-формата для системного криптопровайдера .NET
            string pemBody = privateKeyRaw
                .Replace("-----BEGIN PRIVATE KEY-----", "")
                .Replace("-----END PRIVATE KEY-----", "")
                .Replace("\n", "").Replace("\r", "").Trim();

            byte[] privateKeyBytes = Convert.FromBase64String(pemBody);

            // Подписываем токен алгоритмом RS256
            using (var rsa = System.Security.Cryptography.RSA.Create())
            {
                rsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);
                byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(assertionInput);
                byte[] signatureBytes = rsa.SignData(inputBytes, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                string signature = Convert.ToBase64String(signatureBytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
                string jwtToken = $"{assertionInput}.{signature}";

                // Обмениваем подписанный JWT-токен на реальный рабочий AccessToken
                var dict = new System.Collections.Generic.Dictionary<string, string>
                {
                    { "grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer" },
                    { "assertion", jwtToken }
                };

                var content = new FormUrlEncodedContent(dict);
                var tokenResponse = await _httpClient.PostAsync("https" + ":" + "//" + "oauth2" + "." + "googleapis" + "." + "com" + "/" + "token", content);
                if (!tokenResponse.IsSuccessStatusCode) return string.Empty;

                string tokenJson = await tokenResponse.Content.ReadAsStringAsync();
                var tokenObj = JObject.Parse(tokenJson);
                return tokenObj["access_token"]?.ToString() ?? string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: БЫСТРЫЙ ИНТЕЛЛЕКТУАЛЬНЫЙ СВЕРИТЕЛЬ ДАТЫ ИЗМЕНЕНИЯ ОБЛАКА-->
    // ================================================================= -->
    /// <summary>
    /// Запрашивает у Google Drive API точную дату последнего изменения документа.
    /// Возвращает null, если не удалось достучаться до API.
    /// </summary>
    public async Task<DateTime?> GetCloudModifiedTimeAsync(string url, string authJsonPath)
    {
        string docId = ExtractDocumentId(url);
        if (string.IsNullOrEmpty(docId) || string.IsNullOrEmpty(authJsonPath) || !File.Exists(authJsonPath)) return null;

        try
        {
            string credentialsText = await File.ReadAllTextAsync(authJsonPath);
            var creds = Newtonsoft.Json.Linq.JObject.Parse(credentialsText);
            string clientEmail = creds["client_email"]?.ToString() ?? string.Empty;
            string privateKeyRaw = creds["private_key"]?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(clientEmail) || string.IsNullOrEmpty(privateKeyRaw)) return null;

            string accessToken = await GetGoogleAccessTokenAsync(clientEmail, privateKeyRaw);
            if (string.IsNullOrEmpty(accessToken)) return null;

            // Запрашиваем метаданные файла из Drive API (запрашиваем строго одно поле modifiedTime)
            string driveApiUrl = $"https://googleapis.com{docId}?fields=modifiedTime";
            var request = new HttpRequestMessage(HttpMethod.Get, driveApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            // ================================================================= -->
            // ИСПРАВЛЕНО CS8600: ДОБАВЛЕН NULLABLE СИМВОЛ ? ДЛЯ СТРОКИ МЕТАДАННЫХ-->
            // ================================================================= -->
            string jsonResponse = await response.Content.ReadAsStringAsync();
            var fileMeta = Newtonsoft.Json.Linq.JObject.Parse(jsonResponse);

            // ИСПРАВЛЕНО: Объявляем переменную как string?, полностью удовлетворяя компилятор .NET!
            string? modifiedTimeStr = fileMeta["modifiedTime"]?.ToString();

            if (DateTime.TryParse(modifiedTimeStr, out DateTime cloudTime))
            {
                return cloudTime.ToLocalTime();
            }

        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Google Drive Meta Error] {ex.Message}");
        }
        return null;
    }

}
