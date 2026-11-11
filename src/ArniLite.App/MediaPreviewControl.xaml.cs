using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ArniLite.Core;
using ArniLite.Storage;

namespace ArniLite
{
    public partial class MediaPreviewControl : UserControl
    {
        public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(nameof(SourcePath), typeof(string), typeof(MediaPreviewControl), new PropertyMetadata(string.Empty, SourceChanged));
        public static readonly DependencyProperty IsAudioProperty = DependencyProperty.Register(nameof(IsAudio), typeof(bool), typeof(MediaPreviewControl), new PropertyMetadata(false, SourceChanged));
        private readonly DispatcherTimer _timer;
        private bool _playing;
        private bool _updating;
        private bool _opened;
        private TimeSpan _duration;
        private string _statusKey = "MediaReady";

        public MediaPreviewControl()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => UpdatePosition();
            IsVisibleChanged += (_, _) => { if (!IsVisible) Release(); };
            Unloaded += (_, _) => Release();
            RefreshLocalization();
        }

        public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
        public bool IsAudio { get => (bool)GetValue(IsAudioProperty); set => SetValue(IsAudioProperty, value); }

        private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            var control = (MediaPreviewControl)sender;
            control.Release();
            control.AudioLabel.Visibility = control.IsAudio ? Visibility.Visible : Visibility.Collapsed;
            control.Player.Visibility = control.IsAudio ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Release()
        {
            _timer?.Stop();
            try
            {
                if (Player.Source != null || _opened || _playing) Player.Close();
                Player.Source = null;
            }
            catch (Exception ex) { DiagnosticLog.Current.Write("media_release_failed", exception: ex); }
            _playing = false;
            _opened = false;
            _duration = TimeSpan.Zero;
            _statusKey = "MediaReady";
            Timeline.IsEnabled = false;
            Timeline.Value = 0;
            TimeLabel.Text = "0:00 / --:--";
            RefreshLocalization();
        }

        public void RefreshLocalization()
        {
            MediaStatus.Text = StringCatalog.Get(_statusKey);
            PlayButton.Content = StringCatalog.Get(_playing ? "Pause" : "Play");
        }

        private bool ValidSource() => File.Exists(SourcePath) && PreviewPolicy.Classify(SourcePath) != PreviewKind.Text;

        private void Play_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_playing)
                {
                    Player.Pause();
                    _playing = false;
                    _timer.Stop();
                }
                else
                {
                    if (!ValidSource()) { _statusKey = "MediaMissing"; RefreshLocalization(); return; }
                    if (Player.Source == null) Player.Source = new Uri(SourcePath, UriKind.Absolute);
                    Player.Play();
                    _playing = true;
                    _statusKey = _opened ? "MediaLocalHint" : "MediaOpening";
                    _timer.Start();
                    DiagnosticLog.Current.Write("media_play", phase: IsAudio ? "audio" : "video");
                }
            }
            catch (Exception ex) { PlaybackFailed(ex); }
            RefreshLocalization();
        }

        private void Stop_OnClick(object sender, RoutedEventArgs e)
        {
            try { if (Player.Source != null) Player.Stop(); }
            catch (Exception ex) { PlaybackFailed(ex); return; }
            _playing = false;
            _timer.Stop();
            Timeline.Value = 0;
            UpdatePosition();
            RefreshLocalization();
        }

        private void Player_OnOpened(object sender, RoutedEventArgs e)
        {
            if (Player.Source == null || !IsVisible) return;
            _opened = true;
            _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            Timeline.Maximum = Math.Max(1, _duration.TotalSeconds);
            Timeline.IsEnabled = _duration > TimeSpan.Zero;
            _statusKey = "MediaLocalHint";
            RefreshLocalization();
            UpdatePosition();
        }

        private void Player_OnEnded(object sender, RoutedEventArgs e) => Stop_OnClick(sender, e);

        private void Player_OnFailed(object sender, ExceptionRoutedEventArgs e)
        {
            PlaybackFailed(e.ErrorException);
        }

        private void PlaybackFailed(Exception exception)
        {
            _timer.Stop();
            _playing = false;
            _opened = false;
            _statusKey = "MediaFailed";
            Timeline.IsEnabled = false;
            try { Player.Close(); }
            catch (Exception) { }
            RefreshLocalization();
            DiagnosticLog.Current.Write("media_failed", exception: exception);
        }

        private void UpdatePosition()
        {
            if (!_opened) return;
            _updating = true;
            Timeline.Value = Math.Min(Timeline.Maximum, Player.Position.TotalSeconds);
            _updating = false;
            TimeLabel.Text = Format(Player.Position) + " / " + Format(_duration);
        }

        private static string Format(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

        private void Timeline_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_opened && !_updating)
            {
                try { Player.Position = TimeSpan.FromSeconds(e.NewValue); }
                catch (Exception ex) { PlaybackFailed(ex); }
            }
        }

        private void Volume_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Player != null) Player.Volume = e.NewValue;
        }

        private void External_OnClick(object sender, RoutedEventArgs e)
        {
            if (!ValidSource()) return;
            try { Process.Start(new ProcessStartInfo { FileName = SourcePath, UseShellExecute = true }); }
            catch (Exception ex)
            {
                _statusKey = "MediaFailed";
                RefreshLocalization();
                DiagnosticLog.Current.Write("external_player_failed", exception: ex);
            }
        }
    }
}
