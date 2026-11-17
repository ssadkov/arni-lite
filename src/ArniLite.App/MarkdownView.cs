using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

                var previews = new List<Image>();
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

                    if (TryLocalOrWebLink(run.Link, out var uri))
                    {
                        var link = new Hyperlink(inline)
                        {
                            NavigateUri = uri,
                            Cursor = System.Windows.Input.Cursors.Hand,
                        };
                        link.PreviewMouseLeftButtonUp += (_, args) =>
                        {
                            args.Handled = true;
                            OpenTarget(uri);
                        };
                        text.Inlines.Add(link);
                        if (TryLocalImage(uri, out var preview))
                        {
                            previews.Add(PreviewImage(uri, preview));
                        }
                    }
                    else
                    {
                        text.Inlines.Add(inline);
                    }
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

                for (var previewIndex = 0; previewIndex < previews.Count; previewIndex++)
                {
                    Children.Add(previews[previewIndex]);
                }
            }
        }

        private static Image PreviewImage(Uri uri, BitmapImage source)
        {
            var image = new Image
            {
                Source = source,
                MaxWidth = 480,
                MaxHeight = 360,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 4),
                Cursor = Cursors.Hand,
                ToolTip = uri.LocalPath,
            };
            image.MouseLeftButtonUp += (_, args) =>
            {
                args.Handled = true;
                OpenTarget(uri);
            };
            return image;
        }

        private static bool TryLocalImage(Uri uri, out BitmapImage image)
        {
            image = null;
            if (uri == null || !uri.IsFile)
            {
                return false;
            }

            var path = uri.LocalPath;
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = 960;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryLocalOrWebLink(string target, out Uri uri)
        {
            uri = null;
            var value = (target ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return false;
            }

            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(value, UriKind.Absolute, out uri);
            }

            if (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
            {
                return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsFile;
            }

            return false;
        }

        private static void OpenTarget(Uri uri)
        {
            try
            {
                if (uri == null)
                {
                    return;
                }

                if (uri.IsFile)
                {
                    Process.Start(uri.LocalPath);
                    return;
                }

                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    Process.Start(uri.AbsoluteUri);
                }
            }
            catch (Exception)
            {
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
