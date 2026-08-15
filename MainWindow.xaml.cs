using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SpeakerSync
{
    public class OutputSettings
    {
        public int DeviceNumber { get; set; }
        public bool IsSelected { get; set; }
        public int DelayMs { get; set; }
        public int VolumePercent { get; set; }
    }

    public class AppSettings
    {
        public int InputDeviceIndex { get; set; }
        public int SampleRate { get; set; } = 44100;
        public List<OutputSettings> Outputs { get; set; } = new();
    }

    public partial class MainWindow : Window
    {
        private const int CalibrationBpm = 100;
        private const int CalibrationBeatMs = 60000 / CalibrationBpm;
        private const double CalibrationToleranceMs = 30.0;
        private const int RequiredStableSamples = 8;
        private const int MaxCalibrationWindowSamples = 12;

        private readonly string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpeakerSync",
            "settings.json");

        private readonly AudioEngine engine = new();
        private readonly Dictionary<int, int> measuredDeviceLatencyMs = new();
        private readonly Queue<int> calibrationQueue = new();
        private readonly List<double> liveLatencySamples = new();
        private readonly object calibrationLock = new();

        private bool calibrationRunning;
        private bool autoSyncMode;
        private bool wasAudioRunningBeforeCalibration;
        private bool restoringSettings;
        private int calibrationCurrentDevice = -1;
        private long? expectedBeatTimestamp;
        private long? nextExpectedBeatTimestamp;
        private DispatcherTimer? calibrationTimer;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            KeyDown += Window_KeyDown;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            PopulateDeviceLists();
            SampleRateCombo.ItemsSource = new int[] { 44100, 48000, 96000 };
            SampleRateCombo.SelectedIndex = 0;
            ApplySavedSettings();
            Dispatcher.BeginInvoke(new Action(() => AutoStart()), System.Windows.Threading.DispatcherPriority.Background);
        }

        private bool IsAudioRunning()
        {
            return string.Equals(StartBtn.Content?.ToString(), "Stop", StringComparison.OrdinalIgnoreCase);
        }

        private void PopulateDeviceLists()
        {
            var inputs = engine.GetInputDevices();
            InputCombo.ItemsSource = inputs;

            OutputsPanel.Children.Clear();
            var outputs = engine.GetOutputDevices();
            string? selectedInputName = InputCombo.SelectedItem as string;
            var restoredSettings = LoadSettings();
            var restoredByDevice = restoredSettings?.Outputs.ToDictionary(x => x.DeviceNumber) ?? new Dictionary<int, OutputSettings>();

            for (int actualOutputIndex = 0; actualOutputIndex < outputs.Length; actualOutputIndex++)
            {
                if (!string.IsNullOrWhiteSpace(selectedInputName) && string.Equals(outputs[actualOutputIndex], selectedInputName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var savedSettings = restoredByDevice.TryGetValue(actualOutputIndex, out var outputSettings)
                    ? outputSettings
                    : new OutputSettings { DeviceNumber = actualOutputIndex, DelayMs = 0, VolumePercent = 100, IsSelected = false };

                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
                var cb = new CheckBox { Width = 22, VerticalAlignment = VerticalAlignment.Center, Tag = actualOutputIndex, IsChecked = savedSettings.IsSelected };
                cb.Checked += OutputSelectionChanged;
                cb.Unchecked += OutputSelectionChanged;

                var lbl = new TextBlock { Text = outputs[actualOutputIndex], VerticalAlignment = VerticalAlignment.Center, Width = 300, Foreground = System.Windows.Media.Brushes.LightGray };

                int sliderValue = Math.Clamp(savedSettings.VolumePercent, 0, 200);
                var volSlider = new Slider { Width = 80, Minimum = 0, Maximum = 200, Value = sliderValue, Tag = actualOutputIndex, Margin = new Thickness(8,0,4,0) };
                volSlider.ValueChanged += Volume_ValueChanged;
                var volTextBox = new TextBox { Width = 50, Text = sliderValue.ToString(), Tag = $"vol_tb_{actualOutputIndex}", Margin = new Thickness(0,0,4,0) };
                volTextBox.LostFocus += VolumeTextBox_LostFocus;

                int delayValue = Math.Max(0, savedSettings.DelayMs);
                var delay = new TextBox { Width = 60, Text = delayValue.ToString(), Tag = actualOutputIndex, Margin = new Thickness(8,0,2,0) };
                delay.TextChanged += Delay_TextChanged;
                var delayLabel = new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.LightGray };

                sp.Children.Add(cb);
                sp.Children.Add(lbl);
                sp.Children.Add(volSlider);
                sp.Children.Add(volTextBox);
                sp.Children.Add(delay);
                sp.Children.Add(delayLabel);
                OutputsPanel.Children.Add(sp);
            }

            if (inputs.Length > 0)
            {
                int selectedIndex = restoredSettings?.InputDeviceIndex ?? 0;
                if (selectedIndex < 0 || selectedIndex >= inputs.Length)
                    selectedIndex = 0;
                InputCombo.SelectedIndex = selectedIndex;
            }

            AutoSaveCurrentState();
        }

        private void OutputSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (!restoringSettings)
            {
                SaveSettings();
            }
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            PopulateDeviceLists();
        }

        private void AutoSyncBtn_Click(object sender, RoutedEventArgs e)
        {
            if (calibrationRunning)
            {
                StopCalibrationMode();
                return;
            }

            StartCalibrationForSelectedOutputs(autoSync: true);
        }

        private void StopCalibrationMode()
        {
            calibrationTimer?.Stop();
            calibrationRunning = false;
            calibrationQueue.Clear();
            calibrationCurrentDevice = -1;
            expectedBeatTimestamp = null;
            nextExpectedBeatTimestamp = null;
            liveLatencySamples.Clear();
            AutoSyncBtn.Content = "Auto Sync";
            CalibrationStatusText.Text = "Calibration stopped.";

            if (wasAudioRunningBeforeCalibration)
            {
                RestartAudioIfNeeded();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !calibrationRunning || calibrationCurrentDevice < 0 || !expectedBeatTimestamp.HasValue)
                return;

            e.Handled = true;

            long now = Stopwatch.GetTimestamp();
            double diffMs = GetNearestBeatOffsetMs(now);
            liveLatencySamples.Add(diffMs);

            if (liveLatencySamples.Count > MaxCalibrationWindowSamples)
            {
                liveLatencySamples.RemoveAt(0);
            }

            if (liveLatencySamples.Count >= RequiredStableSamples)
            {
                double median = GetMedian(liveLatencySamples);
                double averageDeviation = liveLatencySamples.Average(v => Math.Abs(v - median));
                double spread = liveLatencySamples.Max() - liveLatencySamples.Min();
                bool stable = averageDeviation <= CalibrationToleranceMs && spread <= CalibrationToleranceMs * 2.0;

                if (stable)
                {
                    int finalLatency = (int)Math.Round(median);
                    measuredDeviceLatencyMs[calibrationCurrentDevice] = finalLatency;
                    calibrationTimer?.Stop();
                    SetLatencyDisplay(calibrationCurrentDevice, finalLatency);
                    CalibrationStatusText.Text = $"Device {calibrationCurrentDevice}: measured {finalLatency} ms (stable).";
                    RunNextCalibrationDevice();
                    return;
                }
            }

            CalibrationStatusText.Text = $"Device {calibrationCurrentDevice}: {diffMs:0} ms (keep tapping to stabilize)";
        }

        private static double GetMedian(List<double> samples)
        {
            var ordered = samples.OrderBy(v => v).ToList();
            int midpoint = ordered.Count / 2;

            if (ordered.Count % 2 == 0)
            {
                return (ordered[midpoint - 1] + ordered[midpoint]) / 2.0;
            }

            return ordered[midpoint];
        }

        private double GetNearestBeatOffsetMs(long now)
        {
            if (!expectedBeatTimestamp.HasValue)
                return 0;

            double previousBeatDiffMs = ((now - expectedBeatTimestamp.Value) * 1000.0) / Stopwatch.Frequency;

            if (!nextExpectedBeatTimestamp.HasValue)
                return previousBeatDiffMs;

            double nextBeatDiffMs = ((now - nextExpectedBeatTimestamp.Value) * 1000.0) / Stopwatch.Frequency;

            if (Math.Abs(nextBeatDiffMs) < Math.Abs(previousBeatDiffMs))
                return nextBeatDiffMs;

            return previousBeatDiffMs;
        }

        private void StartCalibrationForSelectedOutputs(bool autoSync)
        {
            if (calibrationRunning) return;

            var selected = GetSelectedOutputDeviceNumbers();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Select at least one output device before measuring latency.", "No output selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            wasAudioRunningBeforeCalibration = IsAudioRunning();
            if (wasAudioRunningBeforeCalibration)
            {
                engine.Stop();
            }

            AutoSyncBtn.Content = "Stop Calibration";
            StartBtn.IsEnabled = true;
            StartBtn.Content = "Start";

            lock (calibrationLock)
            {
                calibrationRunning = true;
                autoSyncMode = autoSync;
                calibrationQueue.Clear();
                foreach (var item in selected) calibrationQueue.Enqueue(item);
                measuredDeviceLatencyMs.Clear();
                liveLatencySamples.Clear();
                calibrationCurrentDevice = -1;
                expectedBeatTimestamp = null;
                nextExpectedBeatTimestamp = null;
            }

            RunNextCalibrationDevice();
        }

        private void RunNextCalibrationDevice()
        {
            lock (calibrationLock)
            {
                if (calibrationQueue.Count == 0)
                {
                    calibrationRunning = false;
                    AutoSyncBtn.Content = "Auto Sync";

                    if (measuredDeviceLatencyMs.Count > 0)
                    {
                        if (autoSyncMode)
                        {
                            int slowestLatency = measuredDeviceLatencyMs.Values.Max();
                            foreach (var kvp in measuredDeviceLatencyMs)
                            {
                                int targetDelay = Math.Max(0, slowestLatency - kvp.Value);
                                engine.SetOutputDelay(kvp.Key, targetDelay);
                                SetDelayTextForDevice(kvp.Key, targetDelay);
                            }

                            SaveSettings();
                            CalibrationStatusText.Text = $"Auto sync complete. Slowest device latency: {slowestLatency} ms.";
                            RestartAudioIfNeeded();
                            return;
                        }

                        int bestLatency = measuredDeviceLatencyMs.Values.Max();
                        CalibrationStatusText.Text = $"Calibration complete. Slowest measured latency: {bestLatency} ms.";
                        RestartAudioIfNeeded();
                    }
                    else
                    {
                        CalibrationStatusText.Text = "Calibration complete.";
                    }
                    return;
                }

                calibrationCurrentDevice = calibrationQueue.Dequeue();
                liveLatencySamples.Clear();
                expectedBeatTimestamp = null;
            }

            StartSingleDeviceCalibration(calibrationCurrentDevice);
        }

        private void StartSingleDeviceCalibration(int deviceNumber)
        {
            liveLatencySamples.Clear();
            expectedBeatTimestamp = null;
            nextExpectedBeatTimestamp = null;
            CalibrationStatusText.Text = $"Device {deviceNumber}: listen for the 100 BPM sine-wave beep and press Space until the latency stabilizes.";

            calibrationTimer?.Stop();
            calibrationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CalibrationBeatMs) };
            calibrationTimer.Tick += (_, __) => PlayCalibrationTone(deviceNumber);
            calibrationTimer.Start();
            PlayCalibrationTone(deviceNumber);
        }

        private void PlayCalibrationTone(int deviceNumber)
        {
            var tone = new SignalGenerator
            {
                Gain = 0.18,
                Frequency = 220,
                Type = SignalGeneratorType.Sin
            };

            var waveOut = new WaveOutEvent { DeviceNumber = deviceNumber };
            waveOut.Init(tone);
            waveOut.Play();

            var beatTimestamp = Stopwatch.GetTimestamp();
            expectedBeatTimestamp = beatTimestamp;
            nextExpectedBeatTimestamp = beatTimestamp + (long)((CalibrationBeatMs * Stopwatch.Frequency) / 1000.0);

            var stopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            stopTimer.Tick += (_, __) =>
            {
                try
                {
                    waveOut.Stop();
                    waveOut.Dispose();
                }
                catch { }
                stopTimer.Stop();
            };
            stopTimer.Start();
        }

        private List<int> GetSelectedOutputDeviceNumbers()
        {
            var result = new List<int>();
            foreach (StackPanel sp in OutputsPanel.Children)
            {
                if (sp.Children.Count > 0 && sp.Children[0] is CheckBox cb && cb.IsChecked == true && cb.Tag is int deviceNumber)
                    result.Add(deviceNumber);
            }
            return result;
        }

        private void SetDelayTextForDevice(int deviceNumber, int delayMs)
        {
            foreach (StackPanel sp in OutputsPanel.Children)
            {
                if (sp.Children.Count >= 5 && sp.Children[4] is TextBox delayText && delayText.Tag is int tagDevice && tagDevice == deviceNumber)
                {
                    delayText.Text = delayMs.ToString();
                    break;
                }
            }
        }

        private void SetLatencyDisplay(int deviceNumber, int latencyMs)
        {
            foreach (StackPanel sp in OutputsPanel.Children)
            {
                if (sp.Children.Count >= 5 && sp.Children[4] is TextBox delayText && delayText.Tag is int tagDevice && tagDevice == deviceNumber)
                {
                    delayText.Text = latencyMs.ToString();
                    break;
                }
            }
        }

        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is Slider s && s.Tag is int deviceIdx)
            {
                var volumePercent = (float)s.Value;
                var volumeScalar = volumePercent / 100.0f;

                if (IsAudioRunning())
                {
                    engine.SetOutputVolume(deviceIdx, volumeScalar);
                }

                foreach (StackPanel sp in OutputsPanel.Children)
                {
                    if (sp.Children.Count >= 4 && sp.Children[2] is Slider slider && slider == s)
                    {
                        if (sp.Children[3] is TextBox tb) tb.Text = ((int)volumePercent).ToString();
                        break;
                    }
                }

                if (!restoringSettings)
                {
                    SaveSettings();
                }
            }
        }

        private void VolumeTextBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is string tag && tag.StartsWith("vol_tb_"))
            {
                if (int.TryParse(tag.Substring(7), out int idx))
                {
                    if (int.TryParse(tb.Text, out int vol))
                    {
                        vol = Math.Max(0, Math.Min(200, vol));
                        if (IsAudioRunning())
                        {
                            engine.SetOutputVolume(idx, vol / 100.0f);
                        }

                        foreach (StackPanel sp in OutputsPanel.Children)
                        {
                            if (sp.Children.Count >= 4 && sp.Children[3] is TextBox textbox && textbox == tb)
                            {
                                if (sp.Children[2] is Slider slider)
                                {
                                    slider.Value = vol;
                                }
                                break;
                            }
                        }
                        tb.Text = vol.ToString();
                        if (!restoringSettings)
                        {
                            SaveSettings();
                        }
                    }
                }
            }
        }

        private void Delay_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is int deviceIdx)
            {
                if (int.TryParse(tb.Text, out int ms))
                {
                    if (IsAudioRunning())
                    {
                        engine.SetOutputDelay(deviceIdx, Math.Max(0, ms));
                    }

                    if (!restoringSettings)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        private void AutoStart()
        {
            if (InputCombo.Items.Count > 0 && OutputsPanel.Children.Count > 0)
            {
                if (OutputsPanel.Children[0] is StackPanel sp && sp.Children[0] is CheckBox cb)
                {
                    cb.IsChecked = true;
                }

                var e = new RoutedEventArgs(Button.ClickEvent);
                StartBtn_Click(this, e);
            }
        }

        private void InputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!restoringSettings)
            {
                SaveSettings();
            }

            if (IsAudioRunning())
            {
                StopAudioOutput();
                PopulateDeviceLists();
                AutoStart();
            }
            else
            {
                PopulateDeviceLists();
            }
        }

        private void SampleRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!restoringSettings)
            {
                SaveSettings();
            }

            if (IsAudioRunning())
            {
                StopAudioOutput();
                AutoStart();
            }
        }

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (calibrationRunning)
            {
                StopCalibrationMode();
                return;
            }

            if (IsAudioRunning())
            {
                StopAudioOutput();
                return;
            }

            int inputIndex = InputCombo.SelectedIndex;
            if (inputIndex < 0) return;

            var outputs = new List<OutputSpec>();
            foreach (StackPanel sp in OutputsPanel.Children)
            {
                var cb = sp.Children[0] as CheckBox;
                var delay = sp.Children[4] as TextBox;
                if (cb != null && cb.IsChecked == true && cb.Tag is int devIndex)
                {
                    int delayMs = 0;
                    if (delay != null) int.TryParse(delay.Text, out delayMs);
                    outputs.Add(new OutputSpec { DeviceNumber = devIndex, DelayMs = Math.Max(0, delayMs) });
                }
            }

            if (outputs.Count == 0)
            {
                MessageBox.Show(this, "Select at least one output device.", "No outputs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                int sampleRate = 44100;
                if (SampleRateCombo.SelectedItem is int sr) sampleRate = sr;
                engine.Start(inputIndex, outputs, sampleRate);
                StartBtn.Content = "Stop";
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StartBtn.Content = "Start";
            }
        }

        private void StopAudioOutput()
        {
            engine.Stop();
            StartBtn.Content = "Start";
        }

        private void RestartAudioIfNeeded()
        {
            if (!wasAudioRunningBeforeCalibration) return;

            int inputIndex = InputCombo.SelectedIndex;
            if (inputIndex < 0) return;

            var outputs = new List<OutputSpec>();
            foreach (StackPanel sp in OutputsPanel.Children)
            {
                var cb = sp.Children[0] as CheckBox;
                var delay = sp.Children[4] as TextBox;
                if (cb != null && cb.IsChecked == true && cb.Tag is int devIndex)
                {
                    int delayMs = 0;
                    if (delay != null) int.TryParse(delay.Text, out delayMs);
                    outputs.Add(new OutputSpec { DeviceNumber = devIndex, DelayMs = Math.Max(0, delayMs) });
                }
            }

            if (outputs.Count == 0) return;

            try
            {
                int sampleRate = 44100;
                if (SampleRateCombo.SelectedItem is int sr) sampleRate = sr;
                engine.Start(inputIndex, outputs, sampleRate);
                StartBtn.Content = "Stop";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplySavedSettings()
        {
            var saved = LoadSettings();
            if (saved == null) return;

            restoringSettings = true;
            try
            {
                if (InputCombo.Items.Count > 0)
                {
                    int inputIndex = saved.InputDeviceIndex;
                    if (inputIndex >= 0 && inputIndex < InputCombo.Items.Count)
                    {
                        InputCombo.SelectedIndex = inputIndex;
                    }
                }

                if (SampleRateCombo.ItemsSource is int[] rates)
                {
                    int sampleRate = saved.SampleRate;
                    int? matchingRate = rates.FirstOrDefault(r => r == sampleRate);
                    if (matchingRate.HasValue)
                    {
                        SampleRateCombo.SelectedItem = matchingRate.Value;
                    }
                }

                var settingsByDevice = saved.Outputs.ToDictionary(x => x.DeviceNumber);
                foreach (StackPanel sp in OutputsPanel.Children)
                {
                    if (sp.Children.Count >= 5 && sp.Children[0] is CheckBox cb && cb.Tag is int deviceNumber && settingsByDevice.TryGetValue(deviceNumber, out var outputSettings))
                    {
                        cb.IsChecked = outputSettings.IsSelected;
                        if (sp.Children[2] is Slider slider)
                        {
                            slider.Value = Math.Clamp(outputSettings.VolumePercent, 0, 200);
                        }

                        if (sp.Children[3] is TextBox volumeText)
                        {
                            volumeText.Text = Math.Clamp(outputSettings.VolumePercent, 0, 200).ToString();
                        }

                        if (sp.Children[4] is TextBox delayText)
                        {
                            delayText.Text = Math.Max(0, outputSettings.DelayMs).ToString();
                        }
                    }
                }
            }
            finally
            {
                restoringSettings = false;
            }
        }

        private void AutoSaveCurrentState()
        {
            if (!restoringSettings)
            {
                SaveSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath) ?? Environment.CurrentDirectory);

                var outputs = new List<OutputSettings>();
                foreach (StackPanel sp in OutputsPanel.Children)
                {
                    if (sp.Children.Count < 5 || sp.Children[0] is not CheckBox cb || cb.Tag is not int deviceNumber)
                        continue;

                    int volumePercent = 100;
                    if (sp.Children[2] is Slider slider)
                    {
                        volumePercent = (int)Math.Round(slider.Value);
                    }

                    int delayMs = 0;
                    if (sp.Children[4] is TextBox delayText && int.TryParse(delayText.Text, out int parsedDelay))
                    {
                        delayMs = Math.Max(0, parsedDelay);
                    }

                    outputs.Add(new OutputSettings
                    {
                        DeviceNumber = deviceNumber,
                        IsSelected = cb.IsChecked == true,
                        DelayMs = delayMs,
                        VolumePercent = Math.Clamp(volumePercent, 0, 200)
                    });
                }

                var settings = new AppSettings
                {
                    InputDeviceIndex = InputCombo.SelectedIndex,
                    SampleRate = SampleRateCombo.SelectedItem is int sampleRate ? sampleRate : 44100,
                    Outputs = outputs
                };

                File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Keep the app responsive even if settings cannot be persisted.
            }
        }

        private AppSettings? LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsPath))
                    return null;

                var json = File.ReadAllText(settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
