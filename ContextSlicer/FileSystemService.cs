using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ContextSlicer
{
    public class FileSystemService
    {
        private readonly HashSet<string> _excludedDirs = new() { "bin", "obj", ".vs", ".git", ".idea", "node_modules", "publish" };

        public async Task<ObservableCollection<FileNode>> ScanDirectoryAsync(string path)
        {
            return await Task.Run(() =>
            {
                var root = new DirectoryInfo(path);
                return new ObservableCollection<FileNode> { ScanRecursive(root) };
            });
        }

        private FileNode ScanRecursive(DirectoryInfo dir)
        {
            var node = new FileNode
            {
                Name = dir.Name,
                FullPath = dir.FullName,
                IsDirectory = true
            };

            try
            {
                foreach (var subDir in dir.GetDirectories().Where(d => !_excludedDirs.Contains(d.Name.ToLower())))
                {
                    node.Children.Add(ScanRecursive(subDir));
                }

                foreach (var file in dir.GetFiles())
                {
                    node.Children.Add(new FileNode
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false
                    });
                }
            }
            catch (UnauthorizedAccessException) { /* Логгирование или пропуск */ }

            return node;
        }
    }
}
