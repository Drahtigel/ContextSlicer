using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ContextSlicer;
public class FileSystemNode : INotifyPropertyChanged
{
    private bool? _isChecked = false;

    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public FileSystemNode? Parent { get; set; }
    public ObservableCollection<FileSystemNode> Children { get; set; } = new();

    // === НОВЫЕ СВОЙСТВА ДЛЯ СИНТАКСИЧЕСКОГО ДЕРЕВА ===
    public bool IsSyntaxNode { get; set; } = false;          // Флаг, что это элемент кода, а не файл/папка
    public EntryType SyntaxType { get; set; }                // Тип элемента (Class, Method, Section...)
    public string SyntaxSpanInfo { get; set; } = string.Empty; // Координаты куска кода (Start,Length)
    public string EntryPath { get; set; } = string.Empty;     // Уникальный синтаксический путь ("Class.Method")

    // Свойство, возвращающее ключ локализации для префикса (например, "Str_Type_Class")
    // Внутри FileSystemNode.cs обновите свойство TypeLocalKey:
    public string TypeLocalKey
    {
        get
        {
            if (!IsSyntaxNode) return string.Empty;

            // Интеллектуальный поиск SQL префиксов по содержанию расширения файла
            string ext = System.IO.Path.GetExtension(FullPath);
            if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            {
                if (SyntaxType == EntryType.Function) return "Str_Type_SqlFunction";
                if (Name.Contains("[ПРЕДСТАВЛЕНИЕ]") || Name.Contains("[VIEW]")) return "Str_Type_SqlView"; // для надежности фолбэка

                // Смотрим, какой тип был записан, или вычисляем по структуре:
                return "Str_Type_SqlTable";
            }

            return $"Str_Type_{SyntaxType}";
        }
    }


    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, true, true);
    }
    // Добавьте это в FileSystemNode.cs для прямой передачи текста префикса в UI
    public string TypePrefixText
    {
        get
        {
            if (!IsSyntaxNode) return string.Empty;
            // Запрашиваем строку из глобальных ресурсов приложения (с учетом языка)
            if (System.Windows.Application.Current.Resources.Contains(TypeLocalKey))
            {
                return System.Windows.Application.Current.Resources[TypeLocalKey] as string ?? string.Empty;
            }
            return $"[{SyntaxType.ToString().ToUpper()}]"; // Фолбэк на английский апперкейс, если ресурс не успел прогрузиться
        }
    }

    public void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (value == _isChecked) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        // 1. КАСКАД ВНИЗ: Если выбрали узел (папку, файл или класс), проставляем статус всем детям внутри
        if (updateChildren && _isChecked.HasValue)
        {
            foreach (var child in Children)
            {
                child.SetChecked(_isChecked, true, false);
            }
        }

        // 2. КАСКАД ВВЕРХ: Обновляем состояние родительского контейнера (Indeterminate, если выбрано не все)
        if (updateParent && Parent != null)
        {
            Parent.VerifyCheckState();
        }
    }

    public void VerifyCheckState()
    {
        if (Children.Count == 0) return;

        bool hasChecked = false;
        bool hasUnchecked = false;
        bool hasIndeterminate = false;
        bool hasLoadingStub = false; // НОВЫЙ ФЛАГ: Проверка на незавершенную загрузку

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.Name == "LoadingStub...")
            {
                hasLoadingStub = true;
                continue; // Пропускаем заглушку при расчете математики галочек
            }

            if (child.IsChecked == true) hasChecked = true;
            else if (child.IsChecked == false) hasUnchecked = true;
            else hasIndeterminate = true;
        }

        bool? newState;

        // ЖЕСТКОЕ ПРАВИЛО: Если под файлом висит полоса загрузки, структура НЕ полная.
        // Файл не имеет права стать True (выбранным целиком), даже если все текущие видимые дети равны True!
        if (hasLoadingStub)
        {
            // Если хоть что-то выбрано — это строго квадратик Indeterminate (null). Если всё пусто — False.
            newState = (hasChecked || hasIndeterminate) ? (bool?)null : false;
        }
        else
        {
            // Стандартная логика для полностью распарсенных файлов или обычных папок диска
            if (hasIndeterminate)
            {
                newState = null;
            }
            else if (hasChecked && hasUnchecked)
            {
                newState = null;
            }
            else if (hasChecked)
            {
                newState = true; // Файл выбран целиком, только если загрузка завершена и все дети True
            }
            else
            {
                newState = false;
            }
        }

        if (_isChecked != newState)
        {
            _isChecked = newState;
            OnPropertyChanged(nameof(IsChecked));
        }

        // Передаем расчет по цепочке вверх к родительским папкам диска
        Parent?.VerifyCheckState();
    }



    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
