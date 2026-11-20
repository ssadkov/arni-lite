using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArniLite.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArniLite.Core;
using ArniLite.Storage;

namespace ArniLite
{
    public partial class MainWindow : Window
    {
        private bool _stickToBottom = true;
        private bool _scrollScheduled;
        private bool _scrollingToEnd;
        private bool _navigationVisible = true;
        private bool _inspectorVisible;
        private readonly MainViewModel _model;

        public MainWindow(OsFacts os, PreferencesStore? preferences = null)
        {
            InitializeComponent();
            Width = System.Math.Min(Width, SystemParameters.WorkArea.Width);
            Height = System.Math.Min(Height, SystemParameters.WorkArea.Height);
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (icon != null)
                    {
                        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    }
                }
            }
            catch (System.Exception)
            {
            }
            _model = new MainViewModel(os, preferences);
            DataContext = _model;
            _model.Chat.Items.CollectionChanged += ItemsOnCollectionChanged;
            _model.Chat.TextGrew += OnTextGrew;
            StringCatalog.LanguageChanged += OnLanguageChanged;
            DataObject.AddPastingHandler(Draft, Draft_OnPaste);
            Loaded += (_, _) => { ScrollTranscriptToEnd(); Draft.Focus(); _ = _model.Updates.CheckAutomaticallyAsync(); };
            Closed += (_, _) =>
            {
                _model.Chat.Stop();
                _model.DisposeFeatures();
                _model.Chat.Items.CollectionChanged -= ItemsOnCollectionChanged;
                _model.Chat.TextGrew -= OnTextGrew;
                StringCatalog.LanguageChanged -= OnLanguageChanged;
                PreviewPlayer.Release();
            };
        }

        private void OnLanguageChanged(object? sender, System.EventArgs e)
        {
            _model.Chat.RefreshLocalization();
            PreviewPlayer.RefreshLocalization();
        }

        private void Account_OnClick(object sender, RoutedEventArgs e)
        {
            AccountPopup.IsOpen = !AccountPopup.IsOpen;
        }

        private void AccountBilling_OnClick(object sender, RoutedEventArgs e)
        {
            var section = (sender as Button)?.Tag as string ?? "topup";
            AccountPopup.IsOpen = false;
            try { ExternalBrowser.Open("https://chat.arnion.kz/#" + section); }
            catch (AuthLinkException) { }
        }

        private void AccountSignOut_OnClick(object sender, RoutedEventArgs e)
        {
            AccountPopup.IsOpen = false;
        }

        private void Window_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!AccountPopup.IsOpen || !(e.OriginalSource is DependencyObject source)) return;
            if (IsInside(AccountButton, source) || IsInside(AccountPanel, source)) return;
            AccountPopup.IsOpen = false;
        }

        private static bool IsInside(DependencyObject parent, DependencyObject source)
        {
            while (source != null)
            {
                if (source == parent) return true;
                source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source)
                    : LogicalTreeHelper.GetParent(source);
            }
            return false;
        }

        private void Settings_OnClick(object sender, RoutedEventArgs e)
        {
            new SettingsWindow(_model) { Owner = this }.ShowDialog();
        }
        private void Updates_OnClick(object sender, RoutedEventArgs e)
        {
            new SettingsWindow(_model, true) { Owner = this }.ShowDialog();
        }
        private void Dictate_OnClick(object sender, RoutedEventArgs e)
        {
            if (!_model.Dictation.IsInstalled && !_model.Dictation.IsActive)
                new SettingsWindow(_model, speech: true) { Owner = this }.ShowDialog();
            else if (_model.Dictation.ToggleCommand.CanExecute(null)) _model.Dictation.ToggleCommand.Execute(null);
        }

        private void OnTextGrew()
        {
            if (_stickToBottom && !_scrollScheduled)
            {
                _scrollScheduled = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new System.Action(() =>
                {
                    _scrollScheduled = false;
                    if (_stickToBottom) ScrollTranscriptToEnd();
                }));
            }
        }

        private void ItemsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                _stickToBottom = true;
                LatestButton.Visibility = Visibility.Collapsed;
            }
            OnTextGrew();
        }

        private void ScrollTranscriptToEnd()
        {
            if (Transcript.Items.Count == 0) return;
            _scrollingToEnd = true;
            Transcript.ApplyTemplate();
            var scroll = Transcript.Template.FindName("PART_ScrollViewer", Transcript) as ScrollViewer;
            scroll?.ScrollToEnd();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() => _scrollingToEnd = false));
        }

        private void Transcript_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var scroll = e.OriginalSource as ScrollViewer;
            if (scroll == null || scroll.TemplatedParent != Transcript) return;
            if (!_scrollingToEnd && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            {
                _stickToBottom = scroll.ScrollableHeight - scroll.VerticalOffset < 48;
            }
            LatestButton.Visibility = _stickToBottom ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Latest_OnClick(object sender, RoutedEventArgs e)
        {
            _stickToBottom = true;
            LatestButton.Visibility = Visibility.Collapsed;
            ScrollTranscriptToEnd();
        }

        private void ToggleNavigation_OnClick(object sender, RoutedEventArgs e)
        {
            _navigationVisible = !_navigationVisible;
            if (_navigationVisible && ActualWidth < 1120) _inspectorVisible = false;
            UpdatePanels();
        }

        private void ToggleFiles_OnClick(object sender, RoutedEventArgs e)
        {
            _inspectorVisible = !_inspectorVisible;
            UpdatePanels();
        }

        private void Window_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (NavigationColumn != null && InspectorColumn != null && PanelSplitter != null) UpdatePanels();
        }

        private void NewChat_OnClick(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() => Draft.Focus()));
        }

        private void UpdatePanels()
        {
            var showNavigation = _navigationVisible && (!_inspectorVisible || ActualWidth >= 1120);
            NavigationColumn.Width = new GridLength(showNavigation ? 248 : 0);
            Navigation.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
            InspectorColumn.Width = new GridLength(_inspectorVisible ? (ActualWidth >= 1120 ? 340 : 300) : 0);
            Inspector.Visibility = _inspectorVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!_inspectorVisible) PreviewPlayer.Release();
            PanelSplitter.Visibility = Inspector.Visibility;
            FilesButton.Background = _inspectorVisible ? new SolidColorBrush(Color.FromRgb(0xE7, 0xED, 0xE0)) : Brushes.Transparent;
        }

        private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
            {
                if (_model.Chat.NewChatCommand.CanExecute(null)) _model.Chat.NewChatCommand.Execute(null);
                Draft.Focus();
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.B) ToggleNavigation_OnClick(sender, e);
            else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.E) ToggleFiles_OnClick(sender, e);
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
            {
                _navigationVisible = true;
                if (ActualWidth < 1120) _inspectorVisible = false;
                UpdatePanels();
                SearchBox.Focus();
            }
            else return;
            e.Handled = true;
        }

        private void Attach_OnClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = StringCatalog.Get("AddContext"),
                Filter = StringCatalog.Get("AttachFilter"),
            };
            if (dialog.ShowDialog(this) == true)
            {
                _model.Chat.AttachPaths(dialog.FileNames);
            }
        }

        private void Composer_OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Composer_OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _model.Chat.AttachPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            }
        }

        private void Draft_OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (TakeClipboardAttachment(e.DataObject))
            {
                e.CancelCommand();
            }
        }

        private void Draft_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
            if (paste)
            {
                IDataObject data = null;
                try
                {
                    data = Clipboard.GetDataObject();
                }
                catch (System.Exception)
                {
                }

                if (data != null && TakeClipboardAttachment(data))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                var model = DataContext as MainViewModel;
                if (model?.Chat.SendCommand.CanExecute(null) == true)
                {
                    model.Chat.SendCommand.Execute(null);
                }

                e.Handled = true;
            }
        }

        private bool TakeClipboardAttachment(IDataObject data)
        {
            if (data == null)
            {
                return false;
            }

            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                {
                    _model.Chat.AttachPaths(paths);
                    return true;
                }
            }
            catch (System.Exception)
            {
            }

            var image = ReadClipboardImage(data);
            if (image == null)
            {
                return false;
            }

            _model.Chat.AttachImage(image);
            return true;
        }

        private static BitmapSource ReadClipboardImage(IDataObject data)
        {
            foreach (var format in new[] { "PNG", "image/png", "JFIF", "image/jpeg", "image/jpg", "image/bmp", "image/gif", "image/tiff" })
            {
                var frame = DecodeClipboardImage(data, format);
                if (frame != null)
                {
                    return frame;
                }
            }

            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap, false) && data.GetData(DataFormats.Bitmap, false) is BitmapSource bitmap)
                {
                    return FreezeImage(bitmap);
                }
            }
            catch (System.Exception)
            {
            }

            try
            {
                if (Clipboard.ContainsImage())
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        return FreezeImage(image);
                    }
                }
            }
            catch (System.Exception)
            {
            }

            return null;
        }

        private static BitmapSource DecodeClipboardImage(IDataObject data, string format)
        {
            try
            {
                if (!data.GetDataPresent(format, false))
                {
                    return null;
                }

                var raw = data.GetData(format, false);
                Stream stream = raw as Stream;
                if (raw is byte[] bytes)
                {
                    stream = new MemoryStream(bytes);
                }

                if (stream == null)
                {
                    return null;
                }

                var copy = new MemoryStream();
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                stream.CopyTo(copy);
                if (copy.Length < 8)
                {
                    return null;
                }

                copy.Position = 0;
                var decoder = BitmapDecoder.Create(copy, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return decoder.Frames.Count == 0 ? null : FreezeImage(decoder.Frames[0]);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static BitmapSource FreezeImage(BitmapSource source)
        {
            if (source.IsFrozen)
            {
                return source;
            }

            var clone = source.Clone();
            clone.Freeze();
            return clone;
        }
    }
}
