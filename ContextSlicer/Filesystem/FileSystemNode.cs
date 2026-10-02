using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ContextSlicer.Filesystem;
public class FileSystemNode : INotifyPropertyChanged
{
    private bool? _isChecked = false;
    // ================================================================= -->
    // ШАГ 2: АВТОНОМНЫЙ ТРИГГЕР ЛЕНИВОЙ ДОЗАГРУЗКИ СИНТАКСИСА КОДА С ДИСКА -->
    // ================================================================= -->
    private bool _isExpanded = false;

    // Статичное свойство-ссылка на активный менеджер дерева. 
    // Нам нужен этот мост, чтобы ноды могли вызывать асинхронный Roslyn-мерджер
    public static ProjectTreeManager? ActiveManager { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));

                // ЛЕНИВАЯ ЛОГИКА ТВОЕГО АЛГОРИТМА: 
                // Если узел разворачивается, это физический файл на диске (не синтаксический метод),
                // и внутри него всё ещё висит техническая заглушка "LoadingStub..."
                if (_isExpanded && IsFile && !IsSyntaxNode && Children.Any(c => c.Name == "LoadingStub..."))
                {
                    // Запускаем асинхронную догрузку методов в фоновом режиме, не блокируя поток UI
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                    {
                        if (ActiveManager != null)
                        {
                            // Удаляем заглушку перед началом слияния живых нод Roslyn
                            for (int i = Children.Count - 1; i >= 0; i--)
                            {
                                if (Children[i].Name == "LoadingStub...") Children.RemoveAt(i);
                            }

                            // Вызываем монолитный мерджер синтаксиса из менеджера
                            await ActiveManager.PopulateSyntaxNodesAsync(this);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }
    }


    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public bool IsDirectory { get; set; }
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

            // НОВОЕ: Мгновенный перехват литературных типов художественного текста
            if (SyntaxType == EntryType.Tab) return "Str_Type_Tab";
            if (SyntaxType == EntryType.Heading) return "Str_Type_Heading";

            // Существующая логика интеллектуального поиска SQL префиксов
            string ext = System.IO.Path.GetExtension(FullPath);
            if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            {
                if (SyntaxType == EntryType.Function) return "Str_Type_SqlFunction";
                if (Name.Contains("[ПРЕДСТАВЛЕНИЕ]") || Name.Contains("[VIEW]")) return "Str_Type_SqlView";
                return "Str_Type_SqlTable";
            }

            // Базовый фолбэк для стандартных типов исходного кода (.cs, .js и т.д.)
            return $"Str_Type_{SyntaxType}";
        }
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: РАЗДЕЛЕНИЕ РУЧНОГО КЛИКА UI И ПРОГРАММНОГО НАКАТА    -->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: ПАССИВНЫЙ ГЕТТЕР ДЛЯ ИСКЛЮЧЕНИЯ ГОНКИ СОСТОЯНИЙ UI   -->
    // ================================================================= -->
    public bool? IsChecked
    {
        get
        {
            // ИСПРАВЛЕНО: Мы ПОЛНОСТЬЮ УДАЛЯЕМ отсюда вызов метода VerifyCheckState()!
            // Геттер больше не имеет права самовольно менять состояние поля во время отрисовки.
            // Он просто возвращает значение, которое ему ювелирно рассчитал менеджер проекта.
            return _isChecked;
        }
        set
        {
            if (_isChecked != value)
            {
                // Когда пользователь кликает мышкой по чекбоксу на интерфейсе,
                // запускается каноничный каскад обновления вниз к детям и вверх к родителям.
                SetChecked(value, updateChildren: true, updateParent: true);
            }
        }
    }


    /// <summary>
    /// Центральный управляющий метод изменения состояний галочек.
    /// Позволяет жестко контролировать каскадные лавины Tri-State.
    /// </summary>
    public void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        // 1. КАСКАД СВЕРХУ ВНИЗ: Проверяем флаг updateChildren
        if (updateChildren && Children != null && Children.Count > 0)
        {
            // Защита: Если внутри файла висит заглушка ленивой загрузки,
            // мы не имеем права каскадом красить ее внутренности в True!
            bool hasLoadingStub = Children.Any(c => c.Name == "LoadingStub...");

            if (!hasLoadingStub)
            {
                foreach (var child in Children)
                {
                    if (child != null)
                    {
                        // Передаем команду вниз по цепочке
                        child.SetChecked(value, updateChildren: true, updateParent: false);
                    }
                }
            }
        }

        // 2. КАСКАД СНИЗУ ВВЕРХ: Проверяем флаг updateParent
        if (updateParent && Parent != null)
        {
            // Передаем расчет родителю, чтобы он пересчитал свои квадратики тристейта
            Parent.VerifyCheckState();
        }
    }

    // Добавьте это в FileSystemNode.cs для прямой передачи текста префикса в UI
    // ================================================================= -->
    // ИСПРАВЛЕНО: УДАЛЕНИЕ ТЕХНИЧЕСКИХ ПРЕФИКСОВ ДЛЯ ХУДОЖЕСТВЕННОЙ ПРОЗЫ-->
    // ================================================================= -->
    // ================================================================= -->
    // ИСПРАВЛЕНО: УЛЬТРА-КОРОТКИЕ МЕЖДУНАРОДНЫЕ МАРКЕРЫ СИНТАКСИСА КОДА -->
    // ================================================================= -->
    public string TypePrefixText
    {
        get
        {
            if (!IsSyntaxNode) return string.Empty;

            // Для художественной прозы (главы, вкладки) префиксы полностью выключены
            if (SyntaxType == EntryType.Heading || SyntaxType == EntryType.Tab)
            {
                return string.Empty;
            }

            // Математически точное сокращение технических маркеров по ТЗ
            string shortTag = SyntaxType switch
            {
                EntryType.Namespace => "NS",
                EntryType.Class => "CLS",
                EntryType.Function => "FUNC",
                EntryType.Property => "PROP",
                EntryType.Enum => "ENUM", // Мы за тобой наблюдаем!
                EntryType.Struct => "STRCT",
                EntryType.Interface => "INTF",
                EntryType.Section => "SECT",
                _ => SyntaxType.ToString().ToUpper()
            };

            return $"[{shortTag}]";
        }
    }

    
    // ================================================================= -->
    // ИСПРАВЛЕНО: МАТЕМАТИКА TRI-STATE С УЧЕТОМ ТОЧЕЧНЫХ СИНТАКСИЧЕСКИХ НОД -->
    // ================================================================= -->
    public void VerifyCheckState()
    {
        if (Children.Count == 0) return;

        bool hasChecked = false;
        bool hasUnchecked = false;
        bool hasIndeterminate = false;
        bool hasLoadingStub = false;

        // Проверяем, является ли текущий узел файлом, содержащим внутри элементы синтаксиса C#/SQL
        bool isFileWithSyntax = IsFile && !IsSyntaxNode && Children.Any(c => c.IsSyntaxNode);

        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (child.Name == "LoadingStub...")
            {
                hasLoadingStub = true;
                continue;
            }

            if (child.IsChecked == true) hasChecked = true;
            else if (child.IsChecked == false) hasUnchecked = true;
            else hasIndeterminate = true;
        }

        bool? newState;

        // ЖЕСТКОЕ ПРАВИЛО: Если под файлом висит заглушка ИЛИ это файл с частично 
        // загруженным синтаксисом из JSON каркаса, структура еще НЕ полная!
        // Файл не имеет права стать True (выбранным целиком), даже если все текущие видимые дети равны True!
        if (hasLoadingStub || (isFileWithSyntax && hasUnchecked == false && hasChecked && !Children.Any(c => c.SyntaxType == EntryType.Function && c.IsChecked == false)))
        {
            // Проверяем сохраненную конфигурацию модуля. Если в CheckedFiles этого файла нет,
            // но в CheckedEntries есть его функции — он ЖЕСТКО удерживает состояние тристейта (null)!
            var mainVM = System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.MainWindow?.DataContext as MainViewModel);
            bool isSavedAsFullFile = mainVM?.SelectedModule?.CheckedFiles?.Contains(RelativePath) ?? false;

            if (isFileWithSyntax && !isSavedAsFullFile)
            {
                newState = hasChecked || hasIndeterminate ? null : false;
            }
            else
            {
                newState = hasChecked || hasIndeterminate ? null : false;
            }
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
                newState = true;
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
