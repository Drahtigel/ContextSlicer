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

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, true, true);
    }

    private void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (value == _isChecked) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        // Если выбрали папку, проставляем статус всем детям внутри
        if (updateChildren && _isChecked.HasValue)
        {
            foreach (var child in Children)
            {
                child.SetChecked(_isChecked, true, false);
            }
        }

        // Обновляем состояние родительской папки (Indeterminate, если выбрано не все)
        if (updateParent && Parent != null)
        {
            Parent.VerifyCheckState();
        }
    }

    private void VerifyCheckState()
    {
        bool? state = null;
        for (int i = 0; i < Children.Count; i++)
        {
            bool? current = Children[i].IsChecked;
            if (i == 0) state = current;
            else if (state != current)
            {
                state = null; // Indeterminate state
                break;
            }
        }
        SetChecked(state, false, true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
