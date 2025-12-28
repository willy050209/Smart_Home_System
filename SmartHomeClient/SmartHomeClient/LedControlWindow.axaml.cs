using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SmartHomeClient
{
    public partial class LedControlWindow : Window
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiBaseUrl;
        private CancellationTokenSource? _blinkCts;
        private bool _isBlinking = false;
        private bool _suppressUiEvents = false;
        private readonly List<ToggleSwitch> _switches = [];
        private readonly List<Avalonia.Controls.Shapes.Ellipse> _indicators = [];

        public LedControlWindow() { InitializeComponent(); } 

        public LedControlWindow(string baseUrl)
        {
            InitializeComponent();
            _apiBaseUrl = baseUrl;
            _httpClient = new HttpClient { BaseAddress = new Uri(_apiBaseUrl) };

            _switches.Add(this.FindControl<ToggleSwitch>("SwLed1")!);
            _switches.Add(this.FindControl<ToggleSwitch>("SwLed2")!);
            _switches.Add(this.FindControl<ToggleSwitch>("SwLed3")!);
            _switches.Add(this.FindControl<ToggleSwitch>("SwLed4")!);

            _indicators.Add(this.FindControl<Avalonia.Controls.Shapes.Ellipse>("IndLed1")!);
            _indicators.Add(this.FindControl<Avalonia.Controls.Shapes.Ellipse>("IndLed2")!);
            _indicators.Add(this.FindControl<Avalonia.Controls.Shapes.Ellipse>("IndLed3")!);
            _indicators.Add(this.FindControl<Avalonia.Controls.Shapes.Ellipse>("IndLed4")!);

            var slider = this.FindControl<Slider>("SliderSpeed");
            var txtFreq = this.FindControl<TextBlock>("TxtFreq");
            if (slider != null && txtFreq != null)
            {
                slider.PropertyChanged += (s, e) => {
                    if (e.Property.Name == "Value") txtFreq.Text = $"{(int)slider.Value} Hz";
                };
            }

            _ = FetchInitialState();
        }

        private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }

        private async Task FetchInitialState()
        {
            try
            {
                var json = await _httpClient.GetStringAsync("/api/hw/leds");
                var states = JsonSerializer.Deserialize<bool[]>(json);
                if (states != null && states.Length >= 4)
                {
                    _suppressUiEvents = true;
                    try
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            if (i < _switches.Count) _switches[i].IsChecked = states[i];
                            UpdateIndicator(i, states[i]);
                        }
                    }
                    finally { _suppressUiEvents = false; }
                }
            }
            catch { }
        }

        private void UpdateIndicator(int index, bool isOn)
        {
            if (index >= 0 && index < _indicators.Count)
            {
                var classes = _indicators[index].Classes;
                classes.RemoveAll(["ledOn", "ledOff"]);
                classes.Add(isOn ? "ledOn" : "ledOff");
            }
        }

        private async void OnLedToggle(object? sender, RoutedEventArgs e)
        {
            if (_suppressUiEvents || _isBlinking) return;
            if (sender is ToggleSwitch sw && sw.Name != null)
            {
                int ledIndex = int.Parse(sw.Name.Replace("SwLed", ""));
                bool state = sw.IsChecked ?? false;
                UpdateIndicator(ledIndex - 1, state);
                try { await _httpClient.PostAsync($"/api/hw/led/{ledIndex}/{state}", null); } catch { }
            }
        }

        private void OnStartBlink(object sender, RoutedEventArgs e)
        {
            _isBlinking = true;
            this.FindControl<Button>("BtnStartBlink")!.IsEnabled = false;
            this.FindControl<Button>("BtnStopBlink")!.IsEnabled = true;
            foreach (var sw in _switches) sw.IsEnabled = false;

            int mode = this.FindControl<ComboBox>("ComboMode")!.SelectedIndex;
            int freq = (int)this.FindControl<Slider>("SliderSpeed")!.Value;

            _blinkCts = new CancellationTokenSource();
            _ = RunBlinkingSequence(mode, freq, _blinkCts.Token);
        }

        private void OnStopBlink(object sender, RoutedEventArgs e) { StopBlinking(); }

        private void StopBlinking()
        {
            _blinkCts?.Cancel();
            _blinkCts = null;
            _isBlinking = false;
            this.FindControl<Button>("BtnStartBlink")!.IsEnabled = true;
            this.FindControl<Button>("BtnStopBlink")!.IsEnabled = false;
            foreach (var sw in _switches) sw.IsEnabled = true;
            _ = FetchInitialState();
        }

        private async Task RunBlinkingSequence(int mode, int freq, CancellationToken token)
        {
            int delay = 1000 / freq;
            int step = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    switch (mode)
                    {
                        case 0: await SetAllLeds(step % 2 == 0); break;
                        case 1: await SetPattern((step % 4) + 1); break;
                        case 2: bool gA = step % 2 == 0; await SetLedsRaw(gA, !gA, gA, !gA); break;
                        case 3: var r = Random.Shared; await SetLedsRaw(r.Next(2) == 0, r.Next(2) == 0, r.Next(2) == 0, r.Next(2) == 0); break;
                    }
                    step++;
                    await Task.Delay(delay, token);
                }
            }
            catch (TaskCanceledException) { }
        }

        private async Task SetPattern(int activeId) => await SetLedsRaw(activeId == 1, activeId == 2, activeId == 3, activeId == 4);
        private async Task SetAllLeds(bool state) => await SetLedsRaw(state, state, state, state);

        private async Task SetLedsRaw(bool l1, bool l2, bool l3, bool l4)
        {
            bool[] states = { l1, l2, l3, l4 };
            var tasks = new List<Task>();
            for (int i = 0; i < 4; i++)
            {
                int id = i + 1;
                Dispatcher.UIThread.Post(() => UpdateIndicator(id - 1, states[id - 1]));
                tasks.Add(_httpClient.PostAsync($"/api/hw/led/{id}/{states[i]}", null));
            }
            await Task.WhenAll(tasks);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            StopBlinking();
            this.Close();
        }
    }
}