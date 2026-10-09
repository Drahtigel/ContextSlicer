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
    public bool? ComputedState
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
                OnPropertyChanged(nameof(ComputedState));

                // Пробрасываем сигнал клика в центральный Медиатор
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

    /// <summary>
    /// Бронебойный сквозной транслятор. Прошивает всю вертикаль дерева строго сверху вниз.
    /// </summary>
    public void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        // Только конечный лист-глава имеет право физически сохранить флаг
        if (Children == null || Children.Count == 0 || (Children.Count == 1 && Children[0].Name == "LoadingStub..."))
        {
            _isChecked = value;
        }
        else
        {
            _isChecked = null; // Папки никогда не удерживают личный статус в обход детей!
        }

        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(ComputedState));

        // Бескомпромиссный спуск жесткого статуса (true/false) до самых глубоких листьев-глав
        if (updateChildren && Children != null && Children.Count > 0)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child != null && child.Name != "LoadingStub...")
                {
                    child.SetChecked(value, updateChildren: true, updateParent: false);
                }
            }
        }

        // Пересчет баланса по цепочке вверх к корню репозитория
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
