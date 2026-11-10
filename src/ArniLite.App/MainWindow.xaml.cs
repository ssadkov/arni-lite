using System.Windows;
using ArniLite.Core;

namespace ArniLite
{
    public partial class MainWindow : Window
    {
        public MainWindow(OsFacts os)
        {
            InitializeComponent();
            Editor.Text = "Arni Lite G0\nAvalonEdit загружен.\n";
            DataContext = new MainViewModel(os);
        }
    }
}
