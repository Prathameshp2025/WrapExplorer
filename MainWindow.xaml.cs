using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WarpExplorer;

public partial class MainWindow : Window
{
    private string _currentPath;
    private CancellationTokenSource _sizeCalculationCts;
    private bool _isDraggingSelection;
    private Point _selectionStartPoint;

    public ObservableCollection<FileSystemItem> CurrentItems { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        // --- 1. SETUP DATA ---
        CurrentItems = [];
        ListFiles.ItemsSource = CurrentItems;

        // --- 2. MANUALLY WIRE EVENTS (Prevents XamlParseException) ---
        BtnBack.Click += BtnBack_Click;
        BtnUp.Click += BtnUp_Click;
        BtnRefresh.Click += BtnRefresh_Click;
        TxtPath.KeyDown += TxtPath_KeyDown;
        ListDrives.SelectionChanged += ListDrives_SelectionChanged;
        ListFiles.MouseDoubleClick += ListFiles_MouseDoubleClick;

        // Drag Selection Events
        FileListContainer.PreviewMouseLeftButtonDown += FileList_PreviewMouseLeftButtonDown;
        FileListContainer.PreviewMouseMove += FileList_PreviewMouseMove;
        FileListContainer.PreviewMouseLeftButtonUp += FileList_PreviewMouseLeftButtonUp;

        // Context Menu Setup (Code-behind avoids XAML parser issues)
        var contextMenu = new ContextMenu();
        var miOpen = new MenuItem { Header = "Open" };
        miOpen.Click += CtxOpen_Click;
        var miCopy = new MenuItem { Header = "Copy Path" };
        miCopy.Click += CtxCopyPath_Click;
        var miProp = new MenuItem { Header = "Properties" };
        miProp.Click += CtxProperties_Click;

        contextMenu.Items.Add(miOpen);
        contextMenu.Items.Add(miCopy);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(miProp);

        ListFiles.ContextMenu = contextMenu;

        // --- 3. LOAD INITIAL DATA ---
        LoadDrives();
    }

    // --- Loading Logic ---

    private void LoadDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new DriveItem
            {
                Name = d.Name,
                VolumeLabel = d.VolumeLabel,
                TotalSize = d.TotalSize,
                AvailableFreeSpace = d.AvailableFreeSpace
            }).ToList();

            ListDrives.ItemsSource = drives;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Error loading drives: " + ex.Message;
        }
    }

    private async void LoadDirectory(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!Directory.Exists(path))
        {
            MessageBox.Show("Directory does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _sizeCalculationCts?.Cancel();
        _sizeCalculationCts = new CancellationTokenSource();
        var token = _sizeCalculationCts.Token;

        try
        {
            CurrentItems.Clear();
            var dirInfo = new DirectoryInfo(path);
            var foldersToCalculate = new List<FileSystemItem>();

            foreach (var dir in dirInfo.GetDirectories())
            {
                try
                {
                    if ((dir.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    {
                        var item = new FileSystemItem
                        {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            Type = "Folder",
                            Size = -1,
                            DateModified = dir.LastWriteTime,
                            Icon = "📁",
                            IconColor = "#E8B130"
                        };
                        CurrentItems.Add(item);
                        foldersToCalculate.Add(item);
                    }
                }
                catch { }
            }

            foreach (var file in dirInfo.GetFiles())
            {
                try
                {
                    if ((file.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    {
                        CurrentItems.Add(new FileSystemItem
                        {
                            Name = file.Name,
                            FullPath = file.FullName,
                            Type = file.Extension.ToUpper() + " File",
                            Size = file.Length,
                            DateModified = file.LastWriteTime,
                            Icon = "📄",
                            IconColor = "#DDDDDD"
                        });
                    }
                }
                catch { }
            }

            _currentPath = path;
            TxtPath.Text = path;
            TxtStatus.Text = $"{CurrentItems.Count} items";

            await Task.Run(() => CalculateFolderSizes(foldersToCalculate, token), token);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Access Denied to this folder.", "WarpExplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CalculateFolderSizes(List<FileSystemItem> folders, CancellationToken token)
    {
        var parallelOptions = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount };
        try
        {
            Parallel.ForEach(folders, parallelOptions, (item) =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    long size = GetDirectorySize(new DirectoryInfo(item.FullPath), token);
                    Application.Current?.Dispatcher.Invoke(() => item.Size = size);
                }
                catch
                {
                    Application.Current?.Dispatcher.Invoke(() => item.Size = 0);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }

    private long GetDirectorySize(DirectoryInfo d, CancellationToken token)
    {
        long size = 0;
        try
        {
            foreach (var fi in d.EnumerateFiles())
            {
                if (token.IsCancellationRequested) return size;
                size += fi.Length;
            }
            foreach (var di in d.EnumerateDirectories())
            {
                if (token.IsCancellationRequested) return size;
                size += GetDirectorySize(di, token);
            }
        }
        catch { }
        return size;
    }

    // --- Drag Selection Logic ---

    public void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var result = VisualTreeHelper.HitTest(ListFiles, e.GetPosition(ListFiles));
        if (result != null)
        {
            DependencyObject obj = result.VisualHit;
            while (obj != null && obj != ListFiles)
            {
                if (obj is ListViewItem) return;
                if (obj is System.Windows.Controls.Primitives.ScrollBar) return;
                obj = VisualTreeHelper.GetParent(obj);
            }
        }

        _isDraggingSelection = true;
        _selectionStartPoint = e.GetPosition(FileListContainer);

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            ListFiles.SelectedItems.Clear();
        }

        SelectionBox.Width = 0;
        SelectionBox.Height = 0;
        SelectionCanvas.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBox, _selectionStartPoint.X);
        Canvas.SetTop(SelectionBox, _selectionStartPoint.Y);

        FileListContainer.CaptureMouse();
    }

    public void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingSelection) return;

        var currentPoint = e.GetPosition(FileListContainer);

        double x = Math.Min(currentPoint.X, _selectionStartPoint.X);
        double y = Math.Min(currentPoint.Y, _selectionStartPoint.Y);
        double width = Math.Abs(currentPoint.X - _selectionStartPoint.X);
        double height = Math.Abs(currentPoint.Y - _selectionStartPoint.Y);

        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = width;
        SelectionBox.Height = height;

        UpdateSelection(new Rect(x, y, width, height));
    }

    public void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingSelection) StopDragging();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_isDraggingSelection) StopDragging();
    }

    private void StopDragging()
    {
        _isDraggingSelection = false;
        SelectionCanvas.Visibility = Visibility.Collapsed;
        FileListContainer.ReleaseMouseCapture();
    }

    private void UpdateSelection(Rect selectionRect)
    {
        foreach (var item in ListFiles.Items)
        {
            if (ListFiles.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
            {
                var transform = container.TransformToAncestor(FileListContainer);
                var itemBounds = transform.TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

                if (selectionRect.IntersectsWith(itemBounds))
                {
                    if (item is FileSystemItem fsItem) ListFiles.SelectedItems.Add(fsItem);
                }
            }
        }
    }

    // --- Context Menu Logic ---

    public void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ListFiles.SelectedItem is FileSystemItem item) OpenItem(item);
    }

    public void CtxCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ListFiles.SelectedItem is FileSystemItem item) Clipboard.SetText(item.FullPath);
    }

    public void CtxProperties_Click(object sender, RoutedEventArgs e)
    {
        if (ListFiles.SelectedItem is FileSystemItem item)
        {
            string msg = $"Name: {item.Name}\nType: {item.Type}\nSize: {item.SizeDisplay}\nModified: {item.DateModified}";
            MessageBox.Show(msg, "Properties", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // --- Standard Event Handlers ---

    public void ListDrives_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListDrives.SelectedItem is DriveItem drive) LoadDirectory(drive.Name);
    }

    public void ListFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListFiles.SelectedItem is FileSystemItem item) OpenItem(item);
    }

    private void OpenItem(FileSystemItem item)
    {
        if (item.Type == "Folder")
        {
            LoadDirectory(item.FullPath);
        }
        else
        {
            try { Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true }); }
            catch (Exception ex) { TxtStatus.Text = "Error: " + ex.Message; }
        }
    }

    public void BtnBack_Click(object sender, RoutedEventArgs e) => GoUp();
    public void BtnUp_Click(object sender, RoutedEventArgs e) => GoUp();

    private void GoUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        try
        {
            DirectoryInfo parent = Directory.GetParent(_currentPath);
            if (parent != null) LoadDirectory(parent.FullName);
        }
        catch { }
    }

    public void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentPath)) LoadDirectory(_currentPath);
        else LoadDrives();
    }

    public void TxtPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LoadDirectory(TxtPath.Text);
    }
}

// --- Helper Models ---

public class DriveItem
{
    public string Name { get; set; }
    public string VolumeLabel { get; set; }
    public long TotalSize { get; set; }
    public long AvailableFreeSpace { get; set; }
}

public class FileSystemItem : INotifyPropertyChanged
{
    private long _size;
    public string Name { get; set; }
    public string FullPath { get; set; }
    public string Type { get; set; }
    public DateTime DateModified { get; set; }
    public string Icon { get; set; }
    public string IconColor { get; set; }

    public long Size
    {
        get => _size;
        set { if (_size != value) { _size = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeDisplay)); } }
    }

    public string SizeDisplay
    {
        get
        {
            if (Type == "Folder" && Size == -1) return "...";
            if (Size < 1024) return Size + " B";
            if (Size < 1024 * 1024) return (Size / 1024) + " KB";
            if (Size < 1024 * 1024 * 1024) return (Size / (1024 * 1024)) + " MB";
            return (Size / (1024 * 1024 * 1024)) + " GB";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}