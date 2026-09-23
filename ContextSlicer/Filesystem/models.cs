using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

public class FileNode : ObservableObject
{
    // Добавляем = string.Empty; чтобы убрать предупреждения компилятора
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    private bool? _isChecked = false;
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public ObservableCollection<FileNode> Children { get; set; } = new();
}
