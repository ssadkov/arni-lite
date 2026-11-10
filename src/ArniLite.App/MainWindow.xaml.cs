using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArniLite.Core;

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

        public MainWindow(OsFacts os)
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
            _model = new MainViewModel(os);
            DataContext = _model;
            _model.Chat.Items.CollectionChanged += ItemsOnCollectionChanged;
            _model.Chat.TextGrew += OnTextGrew;
            Loaded += (_, _) => { ScrollTranscriptToEnd(); Draft.Focus(); };
            Closed += (_, _) =>
            {
                _model.Chat.Stop();
                _model.Chat.Items.CollectionChanged -= ItemsOnCollectionChanged;
                _model.Chat.TextGrew -= OnTextGrew;
            };
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

        private void Draft_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
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
    }
}
