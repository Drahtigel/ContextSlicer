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

    public static ProjectTreeManager? ActiveManager { get; set; }

    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public bool IsDirectory { get; set; }
    public FileSystemNode? Parent { get; set; }
    public ObservableCollection<FileSystemNode> Children { get; set; } = new();

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

    // ================================================================= -->
    // ИСПРАВЛЕНО: КРИСТАЛЬНО ЧИСТЫЕ ПАССИВНЫЕ СВОЙСТВА НОДЫ ДЛЯ WPF     -->
    // ================================================================= -->
    /// <summary>
    /// Физический флаг отметки элемента структуры.
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
                OnPropertyChanged(nameof(ComputedState));
            }
        }
    }

    /// <summary>
    /// Прямой мост к свойству IsChecked. Полностью исключает внутренние 
    /// Roslyn-блокировки и циклическое затирание данных при ленивой загрузке.
    /// </summary>
    // ================================================================= -->
    // ИСПРАВЛЕНО: ДИНАМИЧЕСКИЙ ГЕТТЕР БАЛАНСА СНИЗУ ВВЕРХ ДЛЯ UI       -->
    // ================================================================= -->
    public bool? ComputedState
    {
        get
        {
            // 1. Если это конечный лист структуры (метод, заголовок или файл без детей) — отдаем его флаг
            if (Children == null || Children.Count == 0 || (Children.Count == 1 && Children[0].Name == "LoadingStub..."))
            {
                return _isChecked;
            }

            // 2. ИСПРАВЛЕНО (ТВОЯ ФОРМУЛА): Динамически вычисляем статус контейнера (класса/namespace/папки) по детям!
            bool hasChecked = false;
            bool hasUnchecked = false;
            bool hasIndeterminate = false;

            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child == null || child.Name == "LoadingStub...") continue;

                // Рекурсивно запрашиваем вычисленное состояние ребенка
                var childState = child.ComputedState;
                if (childState == true) hasChecked = true;
                else if (childState == false) hasUnchecked = true;
                else hasIndeterminate = true;
            }

            // Если у детей каша или квадратик — узел ОБЯЗАН загореться квадратиком (null)!
            if (hasIndeterminate || (hasChecked && hasUnchecked))
                return null;

            // Если абсолютно все дети true — горит галочка
            if (hasChecked && !hasUnchecked)
                return true;

            // Если абсолютно все дети false — пусто
            return false;
        }
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
                OnPropertyChanged(nameof(ComputedState));

                if (ActiveManager != null)
                {
                    if (Children != null && Children.Count > 0)
                    {
                        ActiveManager.RecalculateChildrenCascade(this, value ?? false);
                    }
                    if (Parent != null)
                    {
                        ActiveManager.RecalculateParentsBalance(this.Parent);
                    }
                }
            }
        }
    }



    public void NotifyComputedStateChanged()
    {
        // Выбрасываем стандартные асинхронные уведомления для биндера WPF
        OnPropertyChanged(nameof(ComputedState));
        OnPropertyChanged(nameof(IsChecked));
        Parent?.NotifyComputedStateChanged();
    }

    // ================================================================= -->
    // ИСПРАВЛЕНО: СТЕРИЛЬНЫЙ ПАССИВНЫЙ УСТАНОВИВАТЕЛЬ ФЛАГОВ НОДЫ       -->
    // ================================================================= -->
    /// <summary>
    /// Исторический метод-мост. Теперь просто записывает переданное значение 
    /// без самовольного затирания флагов файлов и папок в null!
    /// </summary>
    public void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        // Нода больше не пытается умничать. Что менеджер приказал — то и пишем!
        _isChecked = value;

        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(ComputedState));

        // Если запрошен каскад вниз силами старых вызывателей
        if (updateChildren && Children != null && Children.Count > 0)
        {
            foreach (var child in Children)
            {
                if (child != null && child.Name != "LoadingStub...")
                {
                    child.SetChecked(value, updateChildren: true, updateParent: false);
                }
            }
        }

        // Если запрошен каскад вверх силами старых вызывателей
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
