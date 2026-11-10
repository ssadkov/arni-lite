using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ArniLite.Core;

namespace ArniLite
{
    public sealed class MarkdownView : StackPanel
    {
        private static readonly Brush DarkText = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C));
        private static readonly Brush LightText = new SolidColorBrush(Color.FromRgb(0xF7, 0xF3, 0xEC));
        private static readonly Brush DarkRule = new SolidColorBrush(Color.FromRgb(0xD5, 0xD1, 0xC9));
        private static readonly Brush LightRule = new SolidColorBrush(Color.FromRgb(0x8A, 0x84, 0x7A));
        private static readonly FontFamily InterfaceFont = new FontFamily("Segoe UI");
        private static readonly FontFamily CodeFont = new FontFamily("Consolas");

        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source),
            typeof(string),
            typeof(MarkdownView),
            new PropertyMetadata(string.Empty, OnChanged));

        public static readonly DependencyProperty LightProperty = DependencyProperty.Register(
            nameof(Light),
            typeof(bool),
            typeof(MarkdownView),
            new PropertyMetadata(false, OnChanged));

        static MarkdownView()
        {
            DarkText.Freeze();
            LightText.Freeze();
            DarkRule.Freeze();
            LightRule.Freeze();
        }

        public MarkdownView()
        {
            Orientation = Orientation.Vertical;
        }

        public string Source
        {
            get => (string)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public bool Light
        {
            get => (bool)GetValue(LightProperty);
            set => SetValue(LightProperty, value);
        }

        private static void OnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ((MarkdownView)sender).Rebuild();
        }

        private void Rebuild()
        {
            Children.Clear();
            var foreground = Light ? LightText : DarkText;
            var blocks = PlainMarkdown.Format(Source);
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.Kind == MarkdownKind.Blank)
                {
                    Children.Add(new Border { Height = 8 });
                    continue;
                }

                if (block.Kind == MarkdownKind.Rule)
                {
                    Children.Add(new Border
                    {
                        Height = 1,
                        Margin = new Thickness(0, 6, 0, 6),
                        Background = Light ? LightRule : DarkRule,
                    });
                    continue;
                }

                var text = new TextBlock
                {
                    Foreground = foreground,
                    TextWrapping = block.Kind == MarkdownKind.Table ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    FontFamily = block.Kind == MarkdownKind.Table ? CodeFont : InterfaceFont,
                    FontSize = FontSizeFor(block),
                    Margin = new Thickness(0, block.Kind == MarkdownKind.Heading ? 8 : 0, 0, 2),
                };
                for (var runIndex = 0; runIndex < block.Runs.Count; runIndex++)
                {
                    var run = block.Runs[runIndex];
                    if (run.Break)
                    {
                        text.Inlines.Add(new LineBreak());
                        continue;
                    }

                    var inline = new Run(run.Text)
                    {
                        FontWeight = run.Bold || block.Kind == MarkdownKind.Heading ? FontWeights.SemiBold : FontWeights.Normal,
                    };
                    if (run.Code && block.Kind != MarkdownKind.Table)
                    {
                        inline.FontFamily = CodeFont;
                    }

                    text.Inlines.Add(inline);
                }

                if (block.Kind == MarkdownKind.Table)
                {
                    Children.Add(new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = text,
                        Margin = new Thickness(0, 4, 0, 6),
                    });
                }
                else
                {
                    Children.Add(text);
                }
            }
        }

        private static double FontSizeFor(MarkdownBlock block)
        {
            if (block.Kind == MarkdownKind.Table)
            {
                return 13;
            }

            if (block.Kind != MarkdownKind.Heading)
            {
                return 14;
            }

            if (block.HeadingLevel <= 1)
            {
                return 18;
            }

            if (block.HeadingLevel == 2)
            {
                return 16;
            }

            return 15;
        }
    }
}
