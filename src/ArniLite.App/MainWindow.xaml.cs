using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArniLite.Core;

namespace ArniLite
{
    public partial class MainWindow : Window
    {
        private bool _stickToBottom = true;

        public MainWindow(OsFacts os)
        {
            InitializeComponent();
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
            var model = new MainViewModel(os);
            DataContext = model;
            model.Chat.Items.CollectionChanged += ItemsOnCollectionChanged;
            model.Chat.TextGrew += OnTextGrew;
        }

        private void OnTextGrew()
        {
            if (_stickToBottom)
            {
                Dispatcher.BeginInvoke(new System.Action(ScrollTranscriptToEnd));
            }
        }

        private void ItemsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (_stickToBottom)
            {
                Dispatcher.BeginInvoke(new System.Action(ScrollTranscriptToEnd));
            }
        }

        private void ScrollTranscriptToEnd()
        {
            ChatScroll.ScrollToEnd();
        }

        private void ChatScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0)
            {
                _stickToBottom = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 48;
            }
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
