using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ContextSlicer
{
    public class ProjectSettings
    {
        public string ProjectName { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string GlobalRules { get; set; } = string.Empty;
        public List<string> SelectedFiles { get; set; } = new();
    }


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

}
