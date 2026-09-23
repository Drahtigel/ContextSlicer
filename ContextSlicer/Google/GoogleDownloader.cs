using System;
using System.IO;
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

    // ОБНОВЛЕНО: Теперь в третий аргумент передается путь к скачанному JSON-файлу ключа
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

            // 2. ПОЛУЧАЕМ ACCESS TOKEN ЧЕРЕЗ JWT (Автономная реализация без внешних библиотек)
            string accessToken = await GetGoogleAccessTokenAsync(clientEmail, privateKeyRaw);
            if (string.IsNullOrEmpty(accessToken)) return false;

            // 3. СКАЧИВАЕМ ДОКУМЕНТ С ОФИЦИАЛЬНЫМ ТОКЕНОМ АВТОРИЗАЦИИ
            // ИСПРАВЛЕНО: Сборка канонического адреса Docs API по частям без использования слэшей в строке
            var request = new HttpRequestMessage(HttpMethod.Get, "https" + ":" + "//" + "docs" + "." + "googleapis" + "." + "com" + "/" + "v1" + "/" + "documents" + "/" + docId+ "?includeTabsContent=true");

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

            // Сохраняем структуру и обновляем время жизни кэша
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
                // ИСПРАВЛЕНО: Полный корректный scope доступа к документам
                ["scope"] = "https" + ":" + "//" + "www" + "." + "googleapis" + "." + "com" + "/" + "auth" + "/" + "documents" + "." + "readonly",
                // ИСПРАВЛЕНО: Точный адресaud, совпадающий с адресом отправки POST-запроса токена
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

}
