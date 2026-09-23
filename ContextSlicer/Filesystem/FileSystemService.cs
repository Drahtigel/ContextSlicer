using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ContextSlicer.Filesystem;

public class FileSystemService
{
    private readonly HashSet<string> _excludedDirs = new() { "bin", "obj", ".vs", ".git", ".idea", "node_modules", "publish" };

    // ИСПРАВЛЕНО: Метод теперь возвращает ObservableCollection из FileSystemNode, убирая конфликт типов
    public async Task<ObservableCollection<FileSystemNode>> ScanDirectoryAsync(string path)
    {
        return await Task.Run(() =>
        {
            var root = new DirectoryInfo(path);
            return new ObservableCollection<FileSystemNode> { ScanRecursive(root, null) };
        });
    }

    // ИСПРАВЛЕНО: Рекурсивный обход теперь строит дерево FileSystemNode и корректно связывает Parent
    private FileSystemNode ScanRecursive(DirectoryInfo dir, FileSystemNode? parent)
    {
        var node = new FileSystemNode
        {
            Name = dir.Name,
            FullPath = dir.FullName,
            IsDirectory = true, // В вашей модели папка определяется как IsFile = false, но для ясности используем поля FileSystemNode
            IsFile = false,
            Parent = parent
        };

        try
        {
            foreach (var subDir in dir.GetDirectories().Where(d => !_excludedDirs.Contains(d.Name.ToLower())))
            {
                node.Children.Add(ScanRecursive(subDir, node));
            }

            foreach (var file in dir.GetFiles())
            {
                node.Children.Add(new FileSystemNode
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsFile = true,
                    Parent = node
                });
            }
        }
        catch (UnauthorizedAccessException) { /* Логгирование или пропуск */ }

        return node;
    }
}
