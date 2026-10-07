using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

public class FileSystemNode : INotifyPropertyChanged
{
    private bool? _isChecked = false;
    private bool _isExpanded = false;

    // Статичное свойство-ссылка на активный менеджер дерева
    public static ProjectTreeManager? ActiveManager { get; set; }

    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public bool IsDirectory { get; set; }
    public FileSystemNode? Parent { get; set; }
    public ObservableCollection<FileSystemNode> Children { get; set; } = new();

    // === СВОЙСТВА ДЛЯ СИНТАКСИЧЕСКОГО ДЕРЕВА ===
    public bool IsSyntaxNode { get; set; } = false;
    public EntryType SyntaxType { get; set; }
    public string SyntaxSpanInfo { get; set; } = string.Empty;
    public string EntryPath { get; set; } = string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));

                // ЛЕГЕНДАРНАЯ ЛЕНИВАЯ ЛОГИКА: Догрузка живого Roslyn при разворачивании стрелочки
                if (_isExpanded && IsFile && !IsSyntaxNode && Children.Any(c => c.Name == "LoadingStub..."))
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Func<Task>(async () =>
                    {
                        if (ActiveManager != null)
                        {
                            for (int i = Children.Count - 1; i >= 0; i--)
                            {
                                if (Children[i].Name == "LoadingStub...") Children.RemoveAt(i);
                            }
                            await ActiveManager.PopulateSyntaxNodesAsync(this);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
        }
    }

    /// <summary>
    /// Сырой флаг состояния из JSON конфигурации. Задается один раз при старте.
    /// </summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));

                // При ручном клике на UI — мгновенно инвалидируем ComputedState вверх по цепочке
                NotifyComputedStateChanged();
            }
        }
    }

    /// <summary>
    /// ИСТОЧНИК ИСТИНЫ ДЛЯ WPF (ТВОЙ АЛГОРИТМ): Органическая, рекурсивная Tri-State логика внутри самой модели!
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: ГВАРДЕЙСКИЙ БАРЬЕР ДЛЯ СВЕРНУТЫХ ФАЙЛОВ С СИНТАКСИСОМ  -->
    // ================================================================= -->
    public bool? ComputedState
    {
        get
        {
            // 1. Если это конечная ветвь-лист синтаксиса, у которой физически нет детей
            if (Children.Count == 0)
            {
                return _isChecked;
            }

            // 2. ИСПРАВЛЕНО (ТВОЙ АЛГОРИТМ): Если это ФАЙЛ КОДА, и внутри него 
            // одновременно присутствуют и заглушка загрузки, и точечные функции —
            // это 100% маркер того, что файл выбран ЧАСТИЧНО! Он обязан вернуть null (квадратик),
            // полностью блокируя ложный возврат True из поломанных JSON конфигураций!
            bool hasLoadingStub = false;
            bool hasRealSyntaxChildren = false;

            for (int i = 0; i < Children.Count; i++)
            {
                if (Children[i].Name == "LoadingStub...") hasLoadingStub = true;
                else if (Children[i].IsSyntaxNode) hasRealSyntaxChildren = true;
            }

            if (IsFile && !IsSyntaxNode && hasRealSyntaxChildren)
            {
                return null; // Жёстко запечатываем файл в состоянии закрашенного квадратика!
            }

            // 3. Фолбэк для чистых нераскрытых файлов (где лежит только заглушка)
            if (hasLoadingStub && Children.Count == 1)
            {
                return _isChecked;
            }

            // 4. Для папок диска — если папка выбрана целиком в CheckedFiles, возвращаем true
            if (!IsFile && !IsSyntaxNode && _isChecked == true)
            {
                return true;
            }

            // 5. Переходим к рекурсивному опросу живых детей для раскрытых веток
            bool hasChecked = false;
            bool hasUnchecked = false;
            bool hasIndeterminate = false;

            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child.Name == "LoadingStub...") continue;

                var childState = child.ComputedState;
                if (childState == true) hasChecked = true;
                else if (childState == false) hasUnchecked = true;
                else hasIndeterminate = true;
            }

            if (hasIndeterminate || (hasChecked && hasUnchecked))
            {
                return null;
            }
            if (hasChecked && !hasUnchecked)
            {
                return true;
            }
            return false;
        }
        set
        {
            SetChecked(value, updateChildren: true, updateParent: true);
        }
    }


    /// <summary>
    /// Прокидывает команду обновления интерфейса вверх к родителям при кликах
    /// </summary>
    public void NotifyComputedStateChanged()
    {
        OnPropertyChanged(nameof(ComputedState));
        Parent?.NotifyComputedStateChanged();
    }

    public void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(ComputedState));

        // Каскад сверху вниз при ручной отметке
        if (updateChildren && Children != null && Children.Count > 0)
        {
            bool hasLoadingStub = Children.Any(c => c.Name == "LoadingStub...");
            if (!hasLoadingStub)
            {
                foreach (var child in Children)
                {
                    child?.SetChecked(value, updateChildren: true, updateParent: false);
                }
            }
        }

        if (updateParent)
        {
            Parent?.NotifyComputedStateChanged();
        }
    }

    public string TypeLocalKey
    {
        get
        {
            if (!IsSyntaxNode) return string.Empty;

            if (SyntaxType == EntryType.Tab) return "Str_Type_Tab";
            if (SyntaxType == EntryType.Heading) return "Str_Type_Heading";

            string ext = System.IO.Path.GetExtension(FullPath);
            if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            {
                if (SyntaxType == EntryType.Function) return "Str_Type_SqlFunction";
                if (Name.Contains("[ПРЕДСТАВЛЕНИЕ]") || Name.Contains("[VIEW]")) return "Str_Type_SqlView";
                return "Str_Type_SqlTable";
            }

            return $"Str_Type_{SyntaxType}";
        }
    }

    public string TypePrefixText
    {
        get
        {
            if (!IsSyntaxNode) return string.Empty;

            if (SyntaxType == EntryType.Heading || SyntaxType == EntryType.Tab) return string.Empty;

            string shortTag = SyntaxType switch
            {
                EntryType.Namespace => "NS",
                EntryType.Class => "CLS",
                EntryType.Function => "FUNC",
                EntryType.Property => "PROP",
                EntryType.Enum => "ENUM",
                EntryType.Struct => "STRCT",
                EntryType.Interface => "INTF",
                EntryType.Section => "SECT",
                _ => SyntaxType.ToString().ToUpper()
            };

            return $"[{shortTag}]";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

