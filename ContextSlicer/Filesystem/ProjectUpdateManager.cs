using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ContextSlicer.Filesystem;

public enum UpdateType
{
    ReadDirectory,
    GoogleApiLoad,
    ConvertDocx
}

public class ProjectUpdateManager
{
    // Таймаут для сетевых задач (15 секунд, согласно лимитам HttpClient)
    private const int NetworkTimeoutSeconds = 15;

    /// <summary>
    /// Функция-заглушка для конвертации DOCX в Google Doc
    /// </summary>
    public async Task<bool> ConvertDocxToGoogleDocPlaceholderAsync(string filePath, CancellationToken token)
    {
        // Имитация асинхронного процесса парсинга/конвертации
        await Task.Delay(2000, token);

        // На данном этапе возвращаем true, при подмене на рабочий код вызовется реальный парсер
        return true;
    }

    /// <summary>
    /// Единый обработчик обновления проекта
    /// </summary>
    public async Task ExecuteUpdateAsync(UpdateType type, string targetPath, CancellationToken token)
    {
        // 1. Включаем индикацию загрузки в UI, блокируем кнопку "Отмена" для предотвращения гонки состояний
        SetUiLoadingState(true);
        UpdateStatusMessage(type);

        try
        {
            switch (type)
            {
                case UpdateType.ReadDirectory:
                    // Сценарий 1: Чтение локального диска или флешки
                    // При извлечении флешки упадет в IOException / UnauthorizedAccessException
                    // Метод ScanDirectoryAsync уже возвращает Task
                    var fileSystem = new FileSystemService();
                    await fileSystem.ScanDirectoryAsync(targetPath);
                    break;

                case UpdateType.GoogleApiLoad:
                    // Сценарий 2: Работа с Google API (проверяем физическую сеть)
                    if (!NetworkInterface.GetIsNetworkAvailable())
                    {
                        throw new WebException("Network is unavailable");
                    }

                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(NetworkTimeoutSeconds));
                        // Вызов метода скачивания из GoogleDownloader
                        // await googleDownloader.DownloadToCacheAsync(...);
                    }
                    break;

                case UpdateType.ConvertDocx:
                    // Сценарий 3: Идентичен первому по типу обработки IO-ошибок
                    bool convertResult = await ConvertDocxToGoogleDocPlaceholderAsync(targetPath, token);
                    if (!convertResult)
                    {
                        throw new FormatException("Bad docx structure");
                    }
                    break;
            }

            // Успешный исход операции
            CloseOverlay();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
        {
            // Обработка сбоев диска/носителей (Сценарии 1 и 3)
            string errorKey = (type == UpdateType.ConvertDocx) ? "Str_Err_Update_Convert" : "Str_Err_Update_ReadDir";
            ShowOverlayError(errorKey, isNetworkError: false);
        }
        catch (Exception ex) when (ex is System.Net.WebException || ex is TaskCanceledException)
        {
            // Обработка сбоев сети и таймаута (Сценарий 2)
            ShowOverlayError("Str_Err_Update_Network", isNetworkError: true);
        }
        finally
        {
            // Разблокируем UI, возвращаем кнопку "Отмена" в активное состояние
            SetUiLoadingState(false);
        }
    }

    private void UpdateStatusMessage(UpdateType type)
    {
        string resourceKey = type switch
        {
            UpdateType.ReadDirectory => "Str_Update_ReadDir",
            UpdateType.GoogleApiLoad => "Str_Update_GoogleApi",
            UpdateType.ConvertDocx => "Str_Update_ConvertDocx",
            _ => "Str_Calculating"
        };

        string message = Application.Current.Resources[resourceKey] as string ?? string.Empty;
        // Передача строки message во View/ViewModel оверлея
    }

    private void ShowOverlayError(string resourceKey, bool isNetworkError)
    {
        string errorMessage = Application.Current.Resources[resourceKey] as string ?? "Unknown Error";
        // 1. Отобразить errorMessage в текстовом поле оверлея

        // 2. Если ошибка сетевая — перевести главную кнопку оверлея в режим "Повторить"
        if (isNetworkError)
        {
            string retryText = Application.Current.Resources["Str_Update_BtnRetry"] as string ?? "Retry";
            // Меняем контент/команду кнопки на повторную отправку
        }
    }

    private void SetUiLoadingState(bool isLoading) { /* Логика управления элементами */ }
    private void CloseOverlay() { /* Логика закрытия */ }
}
