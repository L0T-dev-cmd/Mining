using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Mining.Core;

namespace Mining.Desktop;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(24, 40, 64);
    private static readonly Color Muted = Color.FromArgb(83, 99, 120);
    private static readonly Color Accent = Color.FromArgb(22, 105, 190);
    private readonly MiningSession _session = new();
    private readonly ResourceGuard _guard = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Queue<string> _logLines = new();
    private readonly Channel<string> _pendingLogs = Channel.CreateBounded<string>(
        new BoundedChannelOptions(250) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly TextBox _worker = new() { PlaceholderText = "tu_cuenta.pc01", Dock = DockStyle.Fill };
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _port = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _cpu = new() { Minimum = 5, Maximum = 75, Increment = 5, Dock = DockStyle.Fill };
    private readonly NumericUpDown _threads = new() { Minimum = 1, Maximum = Environment.ProcessorCount, Dock = DockStyle.Fill };
    private readonly CheckBox _battery = new() { Text = "Pausar cuando se use batería", AutoSize = true };
    private readonly CheckBox _busy = new() { Text = "Pausar si el equipo está ocupado", AutoSize = true };
    private readonly Button _start = ActionButton("▶  &Iniciar", Accent, Color.White);
    private readonly Button _stop = ActionButton("■  &Detener", Color.FromArgb(174, 46, 53), Color.White);
    private readonly Button _pause = ActionButton("Ⅱ  &Pausar", Color.FromArgb(226, 233, 242), Ink);
    private readonly Button _save = ActionButton("&Guardar ajustes", Color.FromArgb(226, 233, 242), Ink);
    private readonly Label _status = MetricValue("Detenido");
    private readonly Label _hashrate = MetricValue("0 H/s");
    private readonly Label _shares = MetricValue("0 / 0");
    private readonly Label _elapsed = MetricValue("00:00:00");
    private readonly Label _details = BodyLabel("");
    private readonly Label _resource = BodyLabel("CPU del equipo: —   ·   CPU del programa: —");
    private readonly Label _pauseReason = BodyLabel("La minería solo comienza cuando pulses Iniciar.");
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Consolas", 9), WordWrap = true, AccessibleName = "Registro de actividad"
    };
    private readonly Panel _settingsPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18) };
    private DesktopSettings _settings;
    private bool _manualPause, _active, _transition, _closing, _allowClose;
    private bool _lastAppliedPause;
    private string? _lastPauseReason;

    public MainForm()
    {
        _settings = SettingsStore.Load(out var settingsWarning);
        Text = "Minería Bitcoin · ViaBTC";
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(244, 247, 251);
        ForeColor = Ink;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 900);
        MinimumSize = new Size(940, 700);
        BuildLayout();
        ApplySettings();
        _start.Click += async (_, _) => await StartMiningAsync();
        _stop.Click += async (_, _) => await StopMiningAsync();
        _pause.Click += (_, _) =>
        {
            _manualPause = !_manualPause;
            UpdateResources();
            UpdateDisplay();
        };
        _save.Click += (_, _) => SaveSettings();
        _session.LogMessage += OnSessionLog;
        _timer.Tick += (_, _) => { DrainSessionLog(); UpdateResources(); UpdateDisplay(); };
        FormClosing += OnFormClosing;
        _timer.Start();
        UpdateDisplay();
        AddLog("Aplicación abierta. Minería detenida; inicio automático desactivado.");
        if (settingsWarning is not null) AddLog(settingsWarning);
    }

    private void BuildLayout()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22) };
        Controls.Add(scroll);
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(root);

        var heading = new Label
        {
            Text = "Minería Bitcoin", AutoSize = true, Font = new Font("Segoe UI Semibold", 25),
            ForeColor = Ink, Margin = new Padding(0, 0, 0, 2)
        };
        root.Controls.Add(heading);
        root.Controls.Add(new Label
        {
            Text = "ViaBTC  /  Minería por CPU  /  Tú decides cuándo comenzar",
            AutoSize = true, ForeColor = Muted, Margin = new Padding(2, 0, 0, 16)
        });
        var warning = BodyLabel("Bitcoin se mina principalmente con ASIC. Una CPU tiene un rendimiento muy bajo y puede no conseguir shares ni cubrir la electricidad. El límite de CPU es aproximado; este programa no mide la temperatura.");
        warning.BackColor = Color.FromArgb(255, 245, 218);
        warning.Padding = new Padding(12);
        warning.Height = 78;
        warning.AutoSize = false;
        warning.Dock = DockStyle.Fill;
        root.Controls.Add(warning);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));

        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 98, ColumnCount = 4, Margin = new Padding(0, 12, 0, 12) };
        foreach (var _ in Enumerable.Range(0, 4)) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        metrics.Controls.Add(MetricCard("ESTADO", _status), 0, 0);
        metrics.Controls.Add(MetricCard("VELOCIDAD", _hashrate), 1, 0);
        metrics.Controls.Add(MetricCard("SHARES: ACEPTADAS / RECHAZADAS", _shares), 2, 0);
        metrics.Controls.Add(MetricCard("TIEMPO DE SESIÓN", _elapsed), 3, 0);
        root.Controls.Add(metrics);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 375, ColumnCount = 2, Margin = Padding.Empty };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        middle.Controls.Add(_settingsPanel, 0, 0);
        _settingsPanel.Controls.Add(BuildSettings());
        var operation = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18) };
        middle.Controls.Add(operation, 1, 0);
        operation.Controls.Add(BuildOperation());
        root.Controls.Add(middle);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 390));

        var logBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Height = 44, Margin = new Padding(0, 12, 0, 0) };
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        logBar.Controls.Add(new Label { Text = "Actividad reciente", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        var export = ActionButton("Exportar &registro", Color.White, Ink);
        export.Click += (_, _) => ExportLog();
        logBar.Controls.Add(export, 1, 0);
        root.Controls.Add(logBar);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.Controls.Add(_log);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        var footer = BodyLabel("Al cerrar la ventana, la minería se detiene. Ajustes guardados solo en este usuario de Windows.");
        footer.Margin = new Padding(0, 10, 0, 0);
        footer.Height = 32;
        root.Controls.Add(footer);
    }

    private Control BuildSettings()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = new Label { Text = "Configuración del minero", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        grid.Controls.Add(title, 0, 0);
        grid.SetColumnSpan(title, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        AddField(grid, 1, "&Trabajador", _worker);
        AddField(grid, 2, "&Servidor", _host);
        AddField(grid, 3, "&Puerto", _port);
        AddField(grid, 4, "Objetivo CPU (%)", _cpu);
        AddField(grid, 5, "&Hilos", _threads);
        _port.Items.AddRange([3333, 443, 25]);
        var explanation = BodyLabel("Trabajador: cuentaViaBTC.nombrePC. No escribas tu contraseña ni una dirección de wallet. TCP Stratum: el puerto 443 no implica cifrado TLS.");
        explanation.Font = new Font("Segoe UI", 9);
        explanation.Dock = DockStyle.Fill;
        grid.Controls.Add(explanation, 0, 6);
        grid.SetColumnSpan(explanation, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        grid.Controls.Add(_save, 0, 7);
        grid.SetColumnSpan(_save, 2);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        return grid;
    }

    private Control BuildOperation()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        var title = new Label { Text = "Control y protección del equipo", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        grid.Controls.Add(title);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        grid.Controls.Add(_battery);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        grid.Controls.Add(_busy);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        var protection = BodyLabel("Prioridad baja. Pausa con CPU total ≥85% durante 3 s; vuelve cuando baja a ≤65% durante 5 s. Detén la sesión para cambiar los ajustes.");
        protection.Font = new Font("Segoe UI", 9);
        grid.Controls.Add(protection);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        grid.Controls.Add(_resource);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        for (var i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        actions.Controls.Add(_start, 0, 0);
        actions.Controls.Add(_pause, 1, 0);
        actions.Controls.Add(_stop, 2, 0);
        grid.Controls.Add(actions);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        _pauseReason.ForeColor = Accent;
        grid.Controls.Add(_pauseReason);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        _details.Font = new Font("Segoe UI", 9);
        grid.Controls.Add(_details);
        return grid;
    }

    private static void AddField(TableLayoutPanel grid, int row, string label, Control control)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        control.AccessibleName = label.Replace("&", "");
        grid.Controls.Add(control, 1, row);
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
    }

    private static Panel MetricCard(string title, Label value)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(3) };
        var caption = new Label { Text = title, Dock = DockStyle.Top, Height = 30, ForeColor = Muted, Font = new Font("Segoe UI", 8) };
        value.Dock = DockStyle.Fill;
        card.Controls.Add(value);
        card.Controls.Add(caption);
        return card;
    }

    private static Label MetricValue(string text) => new()
    {
        Text = text, Font = new Font("Segoe UI Semibold", 16), ForeColor = Ink,
        AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label BodyLabel(string text) => new()
    {
        Text = text, ForeColor = Muted, Dock = DockStyle.Fill, AutoSize = false,
        TextAlign = ContentAlignment.TopLeft, Margin = new Padding(0, 3, 0, 3)
    };

    private static Button ActionButton(string text, Color background, Color foreground) => new()
    {
        Text = text, BackColor = background, ForeColor = foreground, FlatStyle = FlatStyle.Flat,
        Dock = DockStyle.Fill, Cursor = Cursors.Hand, Height = 38, UseVisualStyleBackColor = false,
        Margin = new Padding(3), AutoEllipsis = true
    };

    private void ApplySettings()
    {
        _worker.Text = _settings.Worker;
        _host.Text = _settings.Host;
        if (!_port.Items.Contains(_settings.Port)) _port.Items.Add(_settings.Port);
        _port.SelectedItem = _settings.Port;
        _cpu.Value = _settings.CpuPercent;
        _threads.Value = _settings.Threads;
        _battery.Checked = _settings.PauseOnBattery;
        _busy.Checked = _settings.PauseWhenBusy;
    }

    private DesktopSettings ReadSettings() => new()
    {
        Worker = _worker.Text.Trim(), Host = _host.Text.Trim(), Port = (int)(_port.SelectedItem ?? 3333),
        CpuPercent = (int)_cpu.Value, Threads = (int)_threads.Value,
        PauseOnBattery = _battery.Checked, PauseWhenBusy = _busy.Checked
    };

    private void SaveSettings()
    {
        try
        {
            var settings = ReadSettings();
            settings.ToMinerSettings().Validate();
            SettingsStore.Save(settings);
            _settings = settings;
            AddLog("Ajustes guardados. Guardar no inicia la minería.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ShowError("No se pudieron guardar los ajustes", ex.Message);
        }
    }

    private async Task StartMiningAsync()
    {
        if (_active || _transition || _closing) return;
        _transition = true;
        UpdateDisplay();
        try
        {
            var settings = ReadSettings();
            var minerSettings = settings.ToMinerSettings();
            minerSettings.Validate();
            try { SettingsStore.Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AddLog("No se guardaron los ajustes en disco; se utilizarán solo en esta sesión."); }
            _settings = settings;
            _manualPause = false;
            _lastAppliedPause = false;
            _lastPauseReason = null;
            _guard.Reset();
            await _session.StartAsync(minerSettings);
            _active = true;
            UpdateResources();
        }
        catch (Exception ex)
        {
            await _session.StopAsync();
            _active = false;
            ShowError("No se pudo iniciar la minería", ex.Message);
        }
        finally { _transition = false; UpdateDisplay(); }
    }

    private async Task StopMiningAsync()
    {
        if (_transition) return;
        _transition = true;
        UpdateDisplay();
        try
        {
            await _session.StopAsync();
            _active = false;
            _manualPause = false;
            _lastAppliedPause = false;
            _lastPauseReason = null;
            _pauseReason.Text = "Minería detenida. Puedes cambiar los ajustes o iniciar otra sesión.";
        }
        catch (Exception ex) { ShowError("Error al detener la sesión", ex.Message); }
        finally { _transition = false; UpdateDisplay(); }
    }

    private void UpdateResources()
    {
        if (_closing) return;
        var reading = _guard.Read(_settings.PauseOnBattery, _settings.PauseWhenBusy);
        static string Percent(double? value) => value.HasValue ? $"{value.Value:F0}%" : "—";
        _resource.Text = $"CPU del equipo: {Percent(reading.SystemCpuPercent)}   ·   Programa: {Percent(reading.MinerCpuPercent)}\nEnergía: {(reading.OnBattery ? "batería" : "red eléctrica o sin batería")}";
        if (!_active) return;
        var pause = _manualPause || reading.PauseReason is not null;
        var reason = _manualPause ? "pausa manual" : reading.PauseReason;
        if (pause != _lastAppliedPause || reason != _lastPauseReason)
        {
            _session.SetPaused(pause, reason);
            _lastAppliedPause = pause;
            _lastPauseReason = reason;
        }
        _pauseReason.Text = pause ? $"Pausado: {reason}." : "Sesión activa. Puedes pausarla o detenerla en cualquier momento.";
    }

    private void UpdateDisplay()
    {
        var snapshot = _session.Snapshot;
        if (_active && !snapshot.IsRunning && !_transition && !_closing)
        {
            _active = false;
            _manualPause = _lastAppliedPause = false;
            _lastPauseReason = null;
            _pauseReason.Text = snapshot.LastError ?? "Sesión finalizada. Puedes iniciar otra sesión.";
        }
        _status.Text = _closing ? "Cerrando…" : _transition ? "Procesando…" : snapshot.State;
        _hashrate.Text = FormatHashRate(snapshot.HashRate);
        _shares.Text = $"{snapshot.AcceptedShares:N0} / {snapshot.RejectedShares:N0}";
        _elapsed.Text = $"{(int)snapshot.Uptime.TotalHours:00}:{snapshot.Uptime.Minutes:00}:{snapshot.Uptime.Seconds:00}";
        _details.Text = $"Hashes: {snapshot.TotalHashes:N0}  ·  Pendientes: {snapshot.PendingShares}\nReconexiones: {snapshot.Reconnects}  ·  Pool: {(snapshot.IsConnected ? "conectado" : "sin conexión")}";
        _start.Enabled = !_active && !_transition && !_closing;
        _stop.Enabled = _active && !_transition && !_closing;
        _pause.Enabled = _active && !_transition && !_closing;
        _pause.Text = _manualPause ? "▶  &Reanudar" : "Ⅱ  &Pausar";
        _settingsPanel.Enabled = !_active && !_transition && !_closing;
        _battery.Enabled = _busy.Enabled = !_active && !_transition && !_closing;
    }

    private static string FormatHashRate(double rate)
    {
        if (rate >= 1_000_000_000) return $"{rate / 1_000_000_000:F2} GH/s";
        if (rate >= 1_000_000) return $"{rate / 1_000_000:F2} MH/s";
        if (rate >= 1_000) return $"{rate / 1_000:F2} kH/s";
        return $"{Math.Max(0, rate):F0} H/s";
    }

    private void OnSessionLog(string message)
    {
        // A fast or faulty server must not flood the Windows message queue.
        _pendingLogs.Writer.TryWrite(message);
    }

    private void DrainSessionLog()
    {
        var changed = false;
        for (var i = 0; i < 250 && _pendingLogs.Reader.TryRead(out var message); i++)
        {
            AddLog(message, refresh: false);
            changed = true;
        }
        if (changed) RenderLog();
    }

    private void AddLog(string message, bool refresh = true)
    {
        var safe = Regex.Replace(message.Replace('\r', ' ').Replace('\n', ' '),
            @"(?i)(password|contraseña|token|authorization)(\s*[:=]\s*)\S+", "$1$2[oculto]");
        if (safe.Length > 1000) safe = safe[..1000] + "…";
        _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {safe}");
        while (_logLines.Count > 250) _logLines.Dequeue();
        if (refresh) RenderLog();
    }

    private void RenderLog()
    {
        _log.Text = string.Join(Environment.NewLine, _logLines);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Exportar registro de esta sesión", Filter = "Archivo de texto (*.txt)|*.txt",
            FileName = $"mineria-{DateTime.Now:yyyyMMdd-HHmmss}.txt", DefaultExt = "txt", AddExtension = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var contents = string.Join(Environment.NewLine, _logLines);
            if (!string.IsNullOrWhiteSpace(_settings.Worker))
                contents = contents.Replace(_settings.Worker, "[trabajador]", StringComparison.Ordinal);
            File.WriteAllText(dialog.FileName, contents, new UTF8Encoding(false));
            AddLog("Registro exportado. El nombre del trabajador se oculta en el archivo.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowError("No se pudo exportar el registro", ex.Message); }
    }

    private void ShowError(string title, string message)
    {
        AddLog($"{title}: {message}");
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        UpdateDisplay();
        try
        {
            await _session.StopAsync();
            await _session.DisposeAsync();
        }
        finally
        {
            _session.LogMessage -= OnSessionLog;
            _guard.Dispose();
            _timer.Dispose();
            _allowClose = true;
            Close();
        }
    }
}
