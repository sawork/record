using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Lifetime;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VERTEX_PDF_VIEWER.Properties;

namespace VERTEX_PDF_VIEWER
{
    /// <summary>
    /// Главное окно приложения — просмотрщик PDF-файлов из фиксированной корневой папки.
    /// Всё реализовано в code-behind (без MVVM), чтобы было просто и надёжно.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Наблюдатель за изменениями в файловой системе (создание, удаление, переименование)
        private FileSystemWatcher watcher;

        // Таймер для "дебаунса" — собираем события за 200 мс, чтобы не обрабатывать их по одному
        private DispatcherTimer debounceTimer;

        // Очередь событий от FileSystemWatcher
        private readonly List<FileSystemEventArgs> pendingEvents = new List<FileSystemEventArgs>();

        // Блокировка для потокобезопасного доступа к очереди событий
        private readonly object eventLock = new object();

        // Словарь для быстрого поиска TreeViewItem по полному пути к файлу/папке (игнорируя регистр)
        private readonly Dictionary<string, TreeViewItem> nodeIndex = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Конструктор окна
        /// </summary>
        public MainWindow()
        {
            // Инициализация компонентов WPF
            InitializeComponent();

            // Запрет закрытия окна
            WindowStyle = WindowStyle.None;

            // Запрет изменения размера окна
            ResizeMode = ResizeMode.NoResize;

            // Подписываемся на событие раскрытия любого узла дерева (HandledEventsToo = true, чтобы ловить вложенные)
            FileTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnItemExpanded), true);

            // Загружаем корневую папку из настроек
            LoadRoot(Settings.Default.ROOT_FOLDER);

            // Запускаем наблюдение за изменениями в файловой системе
            StartWatcher(Settings.Default.ROOT_FOLDER);

            // Инициализируем WebView2 (асинхронно)
            InitializeWebViewAsync();
        }

        #region Инициализация WebView2

        /// <summary>
        /// Асинхронная инициализация компонента WebView2 для просмотра PDF
        /// </summary>
        private async void InitializeWebViewAsync()
        {
            try
            {
                // Создаём уникальную временную папку для данных пользователя.
                // Это важно: если запустить несколько копий программы одновременно — они не будут конфликтовать.
                string userDataFolder = Path.Combine(Path.GetTempPath(), "VERTEX_PDF_VIEWER", Guid.NewGuid().ToString());
                Directory.CreateDirectory(userDataFolder);

                // Создаём среду выполнения WebView2 с нашей уникальной папкой
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

                // Привязываем среду к элементу управления WebView
                await WebView.EnsureCoreWebView2Async(environment);

                // Отключаем контекстное меню (правой кнопкой)
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                // Скрываем ненужные кнопки в встроенном PDF-тулбаре Edge
                WebView.CoreWebView2.Settings.HiddenPdfToolbarItems =
                    CoreWebView2PdfToolbarItems.Print |
                    CoreWebView2PdfToolbarItems.Save |
                    CoreWebView2PdfToolbarItems.SaveAs |
                    CoreWebView2PdfToolbarItems.MoreSettings |
                    CoreWebView2PdfToolbarItems.FullScreen;

                // Устанавливаем светлую тему для PDF
                WebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации WebView2: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Утилиты

        /// <summary>
        /// Делает первую букву заглавной
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        private static string Capitalize(string text) => text.Length > 0 ? char.ToUpper(text[0]) + text.Substring(1) : "";

        /// <summary>
        /// Приводит путь к единому виду: абсолютный путь без завершающего слеша
        /// Нужно для корректного сравнения путей
        /// </summary>
        private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>
        /// Проверяет, существует ли файл и имеет ли он расширение .pdf (игнорируя регистр)
        /// </summary>
        private static bool IsPdf(string path) => File.Exists(path) && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Загружает PDF в WebView2 по указанному пути
        /// </summary>
        private void LoadPdf(string path)
        {
            // Проверить путь на наличие PDF
            if (IsPdf(path))
            {
                // Загрузить PDF в программу
                WebView.Source = new Uri(path);
            }

            // Вывести сообщение с ошибкой
            else MessageBox.Show($"Не является PDF или файл не найден: {path}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Проверяет наличие доступа к директории
        /// </summary>
        /// <param name="dir"></param>
        /// <returns></returns>
        private bool HasAccess(DirectoryInfo dir)
        {
            try
            {
                // Попытка получить хотя бы один элемент
                using (var enumerator = Directory.EnumerateFileSystemEntries(dir.FullName).GetEnumerator())
                {
                    // Просто пытаемся начать перечисление
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
        #endregion

        #region Построение дерева файлов
        private void LoadRoot(string rootPath)
        {
            // Очищаем текущее содержимое дерева
            FileTree.Items.Clear();

            // Очищаем индекс узлов
            nodeIndex.Clear();

            // Проверяем существование корневой папки
            var rootDir = new DirectoryInfo(rootPath);
            if (!rootDir.Exists)
            {
                MessageBox.Show($"Корневая папка не найдена: {rootPath}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Регистрируем корень в словаре, но НЕ создаём TreeViewItem
            nodeIndex[Normalize(rootDir.FullName)] = null;

            // Получаем список папок в корне (скрытые и системные игнорируем)
            var folders = rootDir.GetDirectories()
                .Where(d =>
                    !d.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System)
                    && HasAccess(d))
                .OrderBy(d => d.Name);

            // Загружаем детей корня напрямую
            foreach (var folder in folders)
            {
                // Создаём элемент папки
                var item = CreateDirectoryItem(folder);

                // Добавляем в корневое дерево
                FileTree.Items.Add(item);
            }

            // Получаем список PDF-файлов в корне
            var pdfs = rootDir.GetFiles("*.pdf").OrderBy(f => f.Name);

            // Загружаем PDF-файлы из корня
            foreach (var pdf in pdfs)
            {
                var item = CreatePdfItem(pdf);
                FileTree.Items.Add(item);
            }

            // Если корень пустой — показываем "[пусто]"
            if (FileTree.Items.Count == 0)
                FileTree.Items.Add(new TreeViewItem { Header = "[пусто]" });
        }

        /// <summary>
        /// Создаёт TreeViewItem для папки
        /// </summary>
        private TreeViewItem CreateDirectoryItem(DirectoryInfo dir)
        {
            // Создаём элемент дерева с заголовком и тегом
            var item = new TreeViewItem
            {
                Header = dir.Name,
                Tag = dir
            };

            // Добавляем заглушку для ленивой загрузки
            item.Items.Add(new TreeViewItem { Header = "[загрузка]" });

            // Регистрируем в словаре
            nodeIndex[Normalize(dir.FullName)] = item;

            // Возвращаем созданный элемент
            return item;
        }

        /// <summary>
        /// Создаёт TreeViewItem для PDF-файла с иконкой
        /// </summary>
        private TreeViewItem CreatePdfItem(FileInfo file)
        {
            // Имя файла без расширения
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file.Name);

            // С заглавной буквы
            string fileNameCapitalize = fileNameWithoutExt;

            // Дата изменения
            string lastWriteDate = file.LastWriteTime.ToString("dd.MM.yyyy");

            // Иконка PDF
            var icon = new Image
            {
                Margin = new Thickness(0, 0, 5, 0),
                Source = (DrawingImage)FindResource("di_pdf_xaml"),
                SnapsToDevicePixels = true,
                Stretch = Stretch.None
            };

            // Название файла
            var fileName = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 0),

                Text = fileNameCapitalize,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Width = 200
            };

            // Дата изменения
            var lastWrite = new TextBlock
            {
                Margin = new Thickness(0, 5, 0, 8),
                Text = $"[{lastWriteDate}]",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray
            };

            // Создаём элемент дерева
            var item = new TreeViewItem
            {
                // Комбинируем иконку и текст в один горизонтальный StackPanel
                Header = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { fileName, lastWrite }
                },

                // Сохраняем FileInfo в теге
                Tag = file
            };

            // При выборе PDF — открываем его
            item.Selected += OnPdfSelected;

            // Регистрируем в словаре
            nodeIndex[Normalize(file.FullName)] = item;

            // Возвращаем созданный элемент
            return item;
        }

        /// <summary>
        /// Обработчик раскрытия узла — ленивая загрузка содержимого папки
        /// </summary>
        private async void OnItemExpanded(object sender, RoutedEventArgs e)
        {
            // Проверяем, что источник — TreeViewItem с DirectoryInfo в теге
            if (e.OriginalSource is TreeViewItem item && item.Tag is DirectoryInfo dir)
                await LoadDirectoryChildren(item, dir);
        }

        /// <summary>
        /// Загружает содержимое папки асинхронно (в фоне), чтобы UI не зависал
        /// </summary>
        private async Task LoadDirectoryChildren(TreeViewItem item, DirectoryInfo dir)
        {
            // Если уже загружено или это не заглушка — выходим
            if (item.Items.Count != 1 || !(item.Items[0] is TreeViewItem placeholder) || placeholder.Header.ToString() != "[загрузка]")
                return;

            item.Items.Clear();

            try
            {
                await Task.Run(() =>
                {
                    // Получаем подпапки
                    var subDirs = dir.GetDirectories()
                    .Where(d =>
                        !d.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System)
                        && HasAccess(d))
                    .OrderBy(d => d.Name);

                    // Получаем только PDF-файлы
                    var pdfs = dir.GetFiles("*.pdf").OrderBy(f => f.Name);

                    // Возвращаемся в UI-поток и добавляем элементы
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var subDir in subDirs)
                            item.Items.Add(CreateDirectoryItem(subDir));

                        foreach (var pdf in pdfs)
                            item.Items.Add(CreatePdfItem(pdf));

                        // Если папка пустая — показываем "[пусто]"
                        if (item.Items.Count == 0)
                            item.Items.Add(new TreeViewItem { Header = "[пусто]" });
                    });
                });
            }
            catch (Exception)
            {
                // При ошибке доступа — показываем сообщение
                item.Items.Add(new TreeViewItem { Header = "[ошибка]" });
            }
        }

        /// <summary>
        /// Обработчик выбора PDF-файла в дереве
        /// </summary>
        private void OnPdfSelected(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem item && item.Tag is FileInfo file)
                LoadPdf(file.FullName);
        }
        #endregion

        #region FileSystemWatcher — отслеживание изменений на диске

        /// <summary>
        /// Запускает наблюдатель за корневой папкой и всеми подпапками
        /// </summary>
        private void StartWatcher(string path)
        {
            watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            // Подписываемся на все типы событий
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;

            // Таймер дебаунса — чтобы не обрабатывать события по одному при массовых изменениях
            debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            debounceTimer.Tick += ProcessPendingEvents;
        }

        /// <summary>
        /// Универсальный обработчик всех событий от FileSystemWatcher
        /// </summary>
        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            lock (eventLock)
                pendingEvents.Add(e);

            debounceTimer.Stop();
            debounceTimer.Start(); // перезапускаем таймер
        }

        /// <summary>
        /// Обрабатываем все накопленные события одним пакетом
        /// </summary>
        private void ProcessPendingEvents(object sender, EventArgs e)
        {
            debounceTimer.Stop();

            List<FileSystemEventArgs> events;
            lock (eventLock)
            {
                events = new List<FileSystemEventArgs>(pendingEvents);
                pendingEvents.Clear();
            }

            foreach (var ev in events)
                HandleFileSystemChange(ev);
        }

        /// <summary>
        /// Основная логика реакции на изменения в файловой системе
        /// </summary>
        private void HandleFileSystemChange(FileSystemEventArgs e)
        {
            string fullPath = Normalize(e.FullPath);
            string parentPath = Normalize(Path.GetDirectoryName(fullPath));

            // Находим родительский узел в дереве
            TreeViewItem parentItem = FindItemByPath(parentPath);
            if (parentItem == null) return; // если не нашли — игнорируем (редкий случай)

            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Created:
                    AddItem(parentItem, fullPath);
                    break;

                case WatcherChangeTypes.Deleted:
                    RemoveItem(fullPath);
                    break;

                case WatcherChangeTypes.Renamed:
                    if (e is RenamedEventArgs renamed)
                        RenameItem(renamed.OldFullPath, renamed.FullPath);
                    break;
            }
        }

        /// <summary>
        /// Быстрый поиск узла по нормализованному пути через словарь
        /// </summary>
        private TreeViewItem FindItemByPath(string path)
        {
            string normalized = Normalize(path);
            nodeIndex.TryGetValue(normalized, out TreeViewItem item);
            return item;
        }

        /// <summary>
        /// Добавление нового файла или папки в дерево
        /// </summary>
        private void AddItem(TreeViewItem parent, string path)
        {
            // Флаг, указывающий, что что-то было добавлено
            bool added = false;

            // Если это папка
            if (Directory.Exists(path))
            {
                // Создаём объект DirectoryInfo
                var dir = new DirectoryInfo(path);

                // Проверяем доступ к папке
                if (HasAccess(dir))
                {
                    // Если до этого был только "[пусто]" — убираем его
                    if (parent.Items.Count == 1 && parent.Items[0] is TreeViewItem child && child.Header is string s && s == "[пусто]")
                        parent.Items.Clear();

                    // Добавляем новую папку
                    parent.Items.Add(CreateDirectoryItem(dir));

                    // Отмечаем, что что-то добавлено
                    added = true;
                }
            }

            // Если это PDF-файл
            else if (IsPdf(path))
            {
                // Создаём объект FileInfo
                var file = new FileInfo(path);

                // Если до этого был только "[пусто]" — убираем его
                if (parent.Items.Count == 1 && parent.Items[0] is TreeViewItem child && child.Header is string s && s == "[пусто]")
                    parent.Items.Clear();

                // Добавляем новый PDF-файл
                parent.Items.Add(CreatePdfItem(file));

                // Отмечаем, что что-то добавлено
                added = true;
            }

            // Если ничего не добавили и родитель пуст — показать "[пусто]"
            if (!added && parent.Items.Count == 0)
            {
                parent.Items.Add(new TreeViewItem { Header = "[пусто]" });
            }
        }

        /// <summary>
        /// Удаление элемента из дерева
        /// </summary>
        private void RemoveItem(string path)
        {
            string normalized = Normalize(path);

            if (!nodeIndex.TryGetValue(normalized, out TreeViewItem item))
                return;

            // Находим родителя и удаляем элемент
            if (item.Parent is TreeViewItem parentItem)
            {
                parentItem.Items.Remove(item);

                // Если родитель стал пустым — добавляем "[пусто]"
                if (parentItem.Items.Count == 0)
                    parentItem.Items.Add(new TreeViewItem { Header = "[пусто]" });
            }
            else if (FileTree.Items.Contains(item)) // если это корневой элемент
            {
                FileTree.Items.Remove(item);
            }

            // Удаляем из словаря
            nodeIndex.Remove(normalized);
        }

        /// <summary>
        /// Обработка переименования или перемещения файла/папки
        /// </summary>
        private void RenameItem(string oldPath, string newPath)
        {
            string oldNormalized = Normalize(oldPath);
            string newNormalized = Normalize(newPath);

            if (!nodeIndex.TryGetValue(oldNormalized, out TreeViewItem item))
                return;

            // Обновляем словарь
            nodeIndex.Remove(oldNormalized);
            nodeIndex[newNormalized] = item;

            // Обновляем отображаемое имя
            item.Header = Path.GetFileName(newPath);

            // Если это папка, проверяем доступ
            if (Directory.Exists(newPath))
            {
                // Создаём объект DirectoryInfo
                var dir = new DirectoryInfo(newPath);

                // Проверяем доступ к папке
                if (!HasAccess(dir))
                {
                    // Папка стала недоступной — удаляем её из дерева
                    RemoveItem(oldPath);
                    return;
                }

                // Обновляем Tag
                item.Tag = dir;
            }
            else
            {
                // Обновляем Tag для файла
                item.Tag = new FileInfo(newPath);
            }

            // Если родитель изменился (перемещение в другую папку)
            string newParentPath = Normalize(Path.GetDirectoryName(newPath));
            TreeViewItem newParent = FindItemByPath(newParentPath);

            if (newParent != null && item.Parent is TreeViewItem oldParent && !ReferenceEquals(newParent, oldParent))
            {
                // Удаляем из старого родителя
                oldParent.Items.Remove(item);
                if (oldParent.Items.Count == 0)
                    oldParent.Items.Add(new TreeViewItem { Header = "[пусто]" });

                // Убираем "[пусто]" у нового родителя, если было
                if (newParent.Items.Count == 1 && newParent.Items[0] is TreeViewItem p && p.Header is string s && s == "[пусто]")
                    newParent.Items.Clear();

                // Добавляем в новый родитель
                newParent.Items.Add(item);
            }
        }

        #endregion

        #region Поведение окна
        /// <summary>
        /// Запрещаем закрытие окна (например, для киоск-режима)
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
        }

        /// <summary>
        /// Отключаем автоматическую прокрутку TreeView к выбранному элементу (чтобы не прыгало)
        /// </summary>
        private void TreeViewItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// Обработчик изменения размера боковой колонки (TreeView)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SideColumn_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Если ширина стала совсем маленькой
            if (e.NewSize.Width < 100 && e.PreviousSize.Width > e.NewSize.Width)
            {
                TreeViewBtn.IsChecked = false;
            }
        }
        #endregion
    }
}
