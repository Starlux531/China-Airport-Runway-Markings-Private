using System.Drawing.Drawing2D;

namespace ChinaRunwayMarkings;

public sealed class MainForm : Form
{
    private readonly Color _navy = Color.FromArgb(18, 31, 49);
    private readonly Color _accent = Color.FromArgb(25, 111, 224);
    private readonly Color _surface = Color.FromArgb(246, 248, 252);
    private readonly Color _border = Color.FromArgb(218, 225, 234);
    private readonly Color _muted = Color.FromArgb(91, 105, 123);

    private readonly TextBox _aptPath = new();
    private readonly TextBox _outputPath = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _filterBox = new();
    private readonly ListView _airportList = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _selectionLabel = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _scanButton = new();
    private readonly Button _recommendedButton = new();
    private readonly Button _clearButton = new();
    private readonly Button _applyButton = new();
    private readonly Button _locateButton = new();
    private readonly Button _restoreButton = new();
    private readonly List<Button> _browseButtons = new();
    private readonly LocalState _localState;
    private AppSettings _settings;
    private string _scannedPath = "";

    private List<AirportRecord> _airports = new();
    private bool _populating;
    private bool _busy;
    private bool _autoScanStarted;
    private int _sortColumn = 1;
    private bool _sortAscending = true;

    public MainForm(LocalState? localState = null)
    {
        _localState = localState ?? new LocalState();
        _settings = _localState.LoadSettings();
        Text = "中国机场跑道标线工具 0.5.0";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1080, 720);
        Size = new Size(1320, 840);
        BackColor = _surface;
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = SystemIcons.Application;

        BuildLayout();
        WireEvents();
        _aptPath.Text = File.Exists(_settings.AptDatPath) ? _settings.AptDatPath : "";
        _outputPath.Text = string.IsNullOrWhiteSpace(_settings.OutputDirectory) ? FindDefaultOutputDirectory() : _settings.OutputDirectory;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = Padding.Empty,
            BackColor = _surface,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        Controls.Add(root);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildPathCard(), 0, 1);
        root.Controls.Add(BuildFilterBar(), 0, 2);
        root.Controls.Add(BuildAirportList(), 0, 3);
        root.Controls.Add(BuildBottomPanel(), 0, 4);
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = _navy, Padding = new Padding(28, 17, 28, 12) };
        var title = new Label
        {
            AutoSize = true,
            Text = "中国机场跑道标线工具",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 19F, FontStyle.Bold),
            Location = new Point(26, 14),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Text = "自动定位并记住 apt.dat · 生成后确认应用 · 自动备份与一键恢复",
            ForeColor = Color.FromArgb(187, 201, 219),
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Location = new Point(29, 57),
        };
        var badge = new Label
        {
            AutoSize = true,
            Text = "确认后应用 · 保留原版备份",
            ForeColor = Color.FromArgb(142, 218, 181),
            BackColor = Color.FromArgb(28, 63, 61),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            Padding = new Padding(10, 5, 10, 5),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        panel.Controls.Add(badge);
        panel.Resize += (_, _) => badge.Location = new Point(panel.ClientSize.Width - badge.Width - 30, 30);
        return panel;
    }

    private Control BuildPathCard()
    {
        var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 10), BackColor = _surface };
        var card = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, BorderColor = _border, Radius = 12, Padding = new Padding(16, 12, 16, 10) };
        outer.Controls.Add(card);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        card.Controls.Add(grid);

        grid.Controls.Add(PathLabel("机场数据库 apt.dat"), 0, 0);
        ConfigurePathBox(_aptPath, "自动查找 Global Airports 的 apt.dat，也可手动选择文件");
        grid.Controls.Add(_aptPath, 1, 0);
        var aptBrowse = SecondaryButton("浏览文件…");
        _browseButtons.Add(aptBrowse);
        aptBrowse.Click += (_, _) => BrowseAptDat();
        grid.Controls.Add(aptBrowse, 2, 0);

        grid.Controls.Add(PathLabel("安全导出目录"), 0, 1);
        ConfigurePathBox(_outputPath, "完整修正版 apt.dat 将生成到这里的独立时间戳目录");
        grid.Controls.Add(_outputPath, 1, 1);
        var outputBrowse = SecondaryButton("浏览文件夹…");
        _browseButtons.Add(outputBrowse);
        outputBrowse.Click += (_, _) => BrowseOutputDirectory();
        grid.Controls.Add(outputBrowse, 2, 1);
        return outer;
    }

    private Control BuildFilterBar()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 5, 22, 5), BackColor = _surface };
        _scanButton.Text = "扫描中国机场";
        StylePrimaryButton(_scanButton);
        _scanButton.SetBounds(22, 5, 140, 40);
        panel.Controls.Add(_scanButton);

        _recommendedButton.Text = "勾选推荐项";
        StyleSecondaryButton(_recommendedButton);
        _recommendedButton.SetBounds(174, 5, 120, 40);
        _recommendedButton.Enabled = false;
        _recommendedButton.Click += (_, _) => SelectRecommended();
        panel.Controls.Add(_recommendedButton);

        _clearButton.Text = "清除勾选";
        StyleSecondaryButton(_clearButton);
        _clearButton.SetBounds(304, 5, 105, 40);
        _clearButton.Enabled = false;
        _clearButton.Click += (_, _) => ClearSelection();
        panel.Controls.Add(_clearButton);

        _filterBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _filterBox.Items.AddRange(new object[] { "全部机场", "建议转换", "已符合标准", "需人工检查", "无/目视标线" });
        _filterBox.SelectedIndex = 0;
        _filterBox.SetBounds(426, 8, 138, 34);
        panel.Controls.Add(_filterBox);

        _locateButton.Text = "查找 apt.dat";
        StyleSecondaryButton(_locateButton);
        _locateButton.SetBounds(578, 5, 120, 40);
        _locateButton.Click += async (_, _) => await LocateAsync();
        panel.Controls.Add(_locateButton);

        _searchBox.PlaceholderText = "搜索 ICAO、机场名或跑道…";
        _searchBox.BorderStyle = BorderStyle.FixedSingle;
        _searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _searchBox.SetBounds(panel.ClientSize.Width - 332, 8, 310, 34);
        panel.Controls.Add(_searchBox);
        panel.Resize += (_, _) => _searchBox.Left = panel.ClientSize.Width - _searchBox.Width - 22;
        return panel;
    }

    private Control BuildAirportList()
    {
        var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 4, 22, 4), BackColor = _surface };
        _airportList.Dock = DockStyle.Fill;
        _airportList.View = View.Details;
        _airportList.FullRowSelect = true;
        _airportList.GridLines = true;
        _airportList.CheckBoxes = true;
        _airportList.HideSelection = false;
        _airportList.BorderStyle = BorderStyle.FixedSingle;
        _airportList.BackColor = Color.White;
        _airportList.Columns.Add("启用", 58);
        _airportList.Columns.Add("ICAO", 76);
        _airportList.Columns.Add("机场", 220);
        _airportList.Columns.Add("跑道", 255);
        _airportList.Columns.Add("源文件标线", 225);
        _airportList.Columns.Add("导出后标线", 225);
        _airportList.Columns.Add("识别结果", 150);
        outer.Controls.Add(_airportList);
        return outer;
    }

    private Control BuildBottomPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 10, 22, 14), BackColor = _surface };
        _summaryLabel.AutoSize = false;
        _summaryLabel.Text = "尚未扫描机场数据库";
        _summaryLabel.ForeColor = _navy;
        _summaryLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _summaryLabel.SetBounds(24, 9, 760, 24);
        panel.Controls.Add(_summaryLabel);

        _selectionLabel.AutoSize = false;
        _selectionLabel.Text = "勾选状态会保留在搜索和筛选之间";
        _selectionLabel.ForeColor = _muted;
        _selectionLabel.SetBounds(24, 36, 760, 22);
        panel.Controls.Add(_selectionLabel);

        _progress.SetBounds(24, 68, 430, 8);
        _progress.Style = ProgressBarStyle.Continuous;
        panel.Controls.Add(_progress);
        _statusLabel.AutoSize = false;
        _statusLabel.Text = "就绪";
        _statusLabel.ForeColor = _muted;
        _statusLabel.SetBounds(466, 61, 360, 24);
        panel.Controls.Add(_statusLabel);

        _applyButton.Text = "生成完整修正版 apt.dat";
        StylePrimaryButton(_applyButton);
        _applyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _applyButton.SetBounds(panel.ClientSize.Width - 282, 18, 260, 48);
        panel.Controls.Add(_applyButton);

        _restoreButton.Text = "一键恢复原版备份";
        StyleSecondaryButton(_restoreButton);
        _restoreButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _restoreButton.SetBounds(panel.ClientSize.Width - 282, 73, 260, 32);
        _restoreButton.Click += async (_, _) => await RestoreAsync();
        panel.Controls.Add(_restoreButton);

        panel.Resize += (_, _) =>
        {
            _applyButton.Left = panel.ClientSize.Width - _applyButton.Width - 22;
            _restoreButton.Left = _applyButton.Left;
            var available = Math.Max(200, _applyButton.Left - 48);
            _summaryLabel.Width = available;
            _selectionLabel.Width = available;
            _progress.Width = Math.Min(320, available / 2);
            _statusLabel.Left = _progress.Right + 12;
            _statusLabel.Width = Math.Max(100, _applyButton.Left - _statusLabel.Left - 16);
        };
        return panel;
    }

    private void WireEvents()
    {
        _scanButton.Click += async (_, _) => await ScanAsync();
        _applyButton.Click += async (_, _) => await ApplyAsync();
        _filterBox.SelectedIndexChanged += (_, _) => RefreshAirportList();
        _searchBox.TextChanged += (_, _) => RefreshAirportList();
        _airportList.ItemCheck += AirportListOnItemCheck;
        _airportList.ItemChecked += AirportListOnItemChecked;
        _airportList.ColumnClick += (_, e) => SortAirportList(e.Column);
        _airportList.DoubleClick += (_, _) => ToggleHighlightedAirports();
        _airportList.KeyDown += AirportListOnKeyDown;
        _aptPath.TextChanged += (_, _) =>
        {
            if (_busy) return;
            _airports.Clear();
            _scannedPath = "";
            RefreshAirportList(false);
            UpdateSummary();
            _restoreButton.Enabled = BackupService.HasBackups(_aptPath.Text.Trim());
        };
        FormClosing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; return; }
            SaveSettings();
        };
        Shown += async (_, _) =>
        {
            if (!_autoScanStarted)
            {
                _autoScanStarted = true;
                if (File.Exists(_aptPath.Text)) await LoadOrScanAsync();
                else await LocateAsync();
            }
        };
    }

    private void SaveSettings()
    {
        try
        {
            _settings.AptDatPath = _aptPath.Text.Trim();
            _settings.XPlaneRoot = XPlaneLocator.RootForAptDat(_settings.AptDatPath) ?? "";
            _settings.OutputDirectory = _outputPath.Text.Trim();
            _localState.SaveSettings(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { _statusLabel.Text = "无法保存路径设置：" + ex.Message; }
    }

    private async Task LocateAsync()
    {
        if (_busy) return;
        SetBusy(true, "正在自动查找 apt.dat…");
        try
        {
            var roots = await Task.Run(() => XPlaneLocator.Discover(_settings.XPlaneRoot));
            if (roots.Count == 0)
            {
                _statusLabel.Text = "未自动找到 apt.dat，可点击“浏览文件…”选择";
                return;
            }
            var files = roots.Select(r => XPlaneLocator.AptDatForRoot(r)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? selected = files[0];
            if (files.Count > 1)
            {
                using var dialog = new Form { Text = "选择要修改的 apt.dat", Size = new Size(960, 300), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
                var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
                list.Items.AddRange(files.Cast<object>().ToArray());
                list.SelectedIndex = 0;
                var ok = new Button { Text = "使用所选 apt.dat", Dock = DockStyle.Bottom, Height = 44, DialogResult = DialogResult.OK };
                dialog.Controls.Add(list);
                dialog.Controls.Add(ok);
                dialog.AcceptButton = ok;
                selected = dialog.ShowDialog(this) == DialogResult.OK ? list.SelectedItem?.ToString() : null;
            }
            if (selected is null) return;
            _aptPath.Text = selected;
            SaveSettings();
        }
        catch (Exception ex) { ShowError("查找失败", ex); }
        finally { SetBusy(false); }
        if (File.Exists(_aptPath.Text)) await LoadOrScanAsync();
    }

    private async Task LoadOrScanAsync()
    {
        var cached = _localState.LoadAirports(_aptPath.Text.Trim());
        if (cached is null) { await ScanAsync(); return; }
        _airports = cached;
        _scannedPath = Path.GetFullPath(_aptPath.Text.Trim());
        RefreshAirportList(false);
        UpdateSortIndicator();
        UpdateSummary();
        SetBusy(false, $"已加载缓存 · {_airports.Count} 个中国机场");
        SaveSettings();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        var path = _aptPath.Text.Trim();
        SetBusy(true, "正在扫描 apt.dat…");
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _progress.Value = p.Percent;
                _statusLabel.Text = $"正在扫描… {p.Percent}% · 已识别 {p.ChinaAirportCount} 个中国机场";
            });
            _airports = await Task.Run(async () =>
            {
                using var stableSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var airports = await AptDatService.ScanChinaAirportsAsync(path, progress);
                try { _localState.SaveAirports(path, airports); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cache is optional. */ }
                return airports;
            });
            _scannedPath = Path.GetFullPath(path);
            SaveSettings();
            RefreshAirportList(persistCurrentChecks: false);
            UpdateSortIndicator();
            UpdateSummary();
            _statusLabel.Text = $"扫描完成 · {_airports.Count} 个中国机场";
        }
        catch (Exception ex)
        {
            _airports.Clear();
            _scannedPath = "";
            RefreshAirportList(false);
            UpdateSummary();
            ShowError("扫描失败", ex);
            _statusLabel.Text = "扫描失败";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyAsync()
    {
        if (_busy) return;
        if (!string.Equals(_scannedPath, Path.GetFullPath(_aptPath.Text.Trim()), StringComparison.OrdinalIgnoreCase))
        {
            await ScanAsync();
            return;
        }
        PersistVisibleChecks();
        var selected = _airports.Where(a => a.Selected && a.HasSuggestedChanges).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先勾选至少一个标记为“建议转换”或“混合标准”的机场。", "尚未选择机场", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var transparent = selected.Where(a => a.HasTransparentRunway).Select(a => a.IcaoCode).ToList();
        if (transparent.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"所选机场中有 {transparent.Count} 个包含透明跑道（{string.Join(", ", transparent.Take(8))}{(transparent.Count > 8 ? "…" : "")}）。\n\n透明跑道的自动标线可能不可见，或与手工贴图重叠。仍要写入完整 apt.dat 副本吗？",
                "透明跑道警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }

        SetBusy(true, "正在生成完整 apt.dat 副本…");
        try
        {
            var progress = new Progress<BuildProgress>(p =>
            {
                _progress.Value = p.Percent;
                _statusLabel.Text = $"正在复制并转换… {p.Percent}% · 已匹配 {p.AirportsWritten} 个机场";
            });
            var sourcePath = _aptPath.Text.Trim();
            var outputPath = _outputPath.Text.Trim();
            var result = await Task.Run(() => AptDatService.ExportModifiedAptDatAsync(
                sourcePath, outputPath, selected, progress));
            RefreshAirportList(persistCurrentChecks: false);
            UpdateSummary();
            _statusLabel.Text = "完整修正版 apt.dat 已导出 · X-Plane 未被修改";
            var answer = MessageBox.Show(this,
                $"完整修正版 apt.dat 已生成。\n\n修改机场：{result.AirportsWritten} 个\n转换跑道端：{result.RunwayEndsChanged} 个\n\n是否备份并替换以下原文件？\n{sourcePath}\n\n选择“是”：校验并保存原版备份，再应用修改；下次启动 X-Plane 生效。请先退出 X-Plane。\n选择“否”：仅保留导出副本。\n\n导出位置：{result.AptDatPath}",
                "是否立即应用标线？", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes)
            {
                _statusLabel.Text = "正在校验、备份并应用标线…";
                var backup = await Task.Run(() => BackupService.Apply(sourcePath, result));
                _airports.Clear();
                _scannedPath = "";
                RefreshAirportList(false);
                UpdateSummary();
                _statusLabel.Text = "标线已应用 · 原版备份已保存";
                MessageBox.Show(this, $"已替换：{sourcePath}\n\n原版备份：{backup}\n\n下次启动 X-Plane 生效；可使用“一键恢复原版备份”回退。", "应用完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            ShowError("生成或应用失败", ex);
            _statusLabel.Text = "操作未完成，请查看错误信息；已生成的备份仍保留";
        }
        finally
        {
            SetBusy(false);
        }
        if (File.Exists(_aptPath.Text) && _scannedPath == "") await ScanAsync();
    }

    private async Task RestoreAsync()
    {
        if (_busy) return;
        var path = _aptPath.Text.Trim();
        if (MessageBox.Show(this, $"恢复以下文件到首次应用前的原版？\n\n{path}\n\n请先完全退出 X-Plane。当前修改版也会保留在备份目录中。", "恢复原版备份", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        SetBusy(true, "正在校验并恢复原版备份…");
        var restored = false;
        try
        {
            var message = await Task.Run(() => BackupService.Restore(path));
            restored = true;
            MessageBox.Show(this, message, "恢复完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ShowError("恢复失败", ex); }
        finally { SetBusy(false); }
        if (restored) await ScanAsync();
    }

    private void RefreshAirportList(bool persistCurrentChecks = true)
    {
        if (_airportList is null) return;
        if (persistCurrentChecks) PersistVisibleChecks();
        var search = _searchBox.Text.Trim();
        var filter = _filterBox.SelectedIndex;
        IEnumerable<AirportRecord> filtered = _airports;

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(a =>
                a.IcaoCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                a.RunwaySummary.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        filtered = filter switch
        {
            1 => filtered.Where(a => a.HasSuggestedChanges && !a.HasTransparentRunway),
            2 => filtered.Where(a => !a.HasSuggestedChanges && a.HasEasaMarkings && !a.HasTransparentRunway),
            3 => filtered.Where(a => a.HasTransparentRunway || a.HasOtherAutomaticMarkings),
            4 => filtered.Where(a => a.Runways.Count > 0 && !a.HasSuggestedChanges && !a.HasEasaMarkings && !a.HasOtherAutomaticMarkings),
            _ => filtered,
        };

        _populating = true;
        _airportList.BeginUpdate();
        try
        {
            _airportList.Items.Clear();
            foreach (var airport in filtered)
            {
                var item = new ListViewItem("")
                {
                    Tag = airport,
                    Checked = airport.Selected,
                    UseItemStyleForSubItems = true,
                };
                item.SubItems.Add(airport.IcaoCode);
                item.SubItems.Add(airport.Name);
                item.SubItems.Add(airport.RunwaySummary);
                item.SubItems.Add(airport.CurrentSummary);
                item.SubItems.Add(airport.SuggestedSummary);
                item.SubItems.Add(airport.Status);

                if (!airport.HasSuggestedChanges) item.ForeColor = Color.FromArgb(125, 135, 148);
                else if (airport.HasTransparentRunway) item.ForeColor = Color.FromArgb(170, 96, 20);
                else item.ForeColor = _navy;
                _airportList.Items.Add(item);
            }
        }
        finally
        {
            _airportList.EndUpdate();
            _populating = false;
        }
        UpdateSelectionLabel();
    }

    private void AirportListOnItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_populating || e.Index < 0 || e.Index >= _airportList.Items.Count ||
            _airportList.Items[e.Index].Tag is not AirportRecord airport) return;
        if (!airport.HasSuggestedChanges && e.NewValue == CheckState.Checked)
        {
            e.NewValue = CheckState.Unchecked;
            return;
        }
        airport.Selected = e.NewValue == CheckState.Checked;
        if (IsHandleCreated) BeginInvoke(UpdateSelectionLabel);
    }

    private void AirportListOnItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (_populating || e.Item.Tag is not AirportRecord airport) return;
        airport.Selected = airport.HasSuggestedChanges && e.Item.Checked;
        if (IsHandleCreated) BeginInvoke(UpdateSelectionLabel);
    }
    private void PersistVisibleChecks()
    {
        if (_populating) return;
        foreach (ListViewItem item in _airportList.Items)
        {
            if (item.Tag is AirportRecord airport && airport.HasSuggestedChanges) airport.Selected = item.Checked;
        }
    }

    private void SelectRecommended()
    {
        PersistVisibleChecks();
        foreach (var airport in _airports) airport.Selected = airport.HasSuggestedChanges && !airport.HasTransparentRunway;
        RefreshAirportList(persistCurrentChecks: false);
    }

    private void ClearSelection()
    {
        foreach (var airport in _airports) airport.Selected = false;
        RefreshAirportList(persistCurrentChecks: false);
    }

    private void UpdateSummary()
    {
        var runwayCount = _airports.Sum(a => a.Runways.Count);
        var ends = _airports.SelectMany(a => a.Ends).ToList();
        var convertible = _airports.Count(a => a.HasSuggestedChanges);
        var transparent = _airports.Count(a => a.HasTransparentRunway);
        _summaryLabel.Text = $"共 {_airports.Count} 个中国机场 · {runwayCount} 条跑道 / {ends.Count} 个跑道端 · {convertible} 个机场可转换 · {transparent} 个含透明跑道";
        UpdateSelectionLabel();
    }

    private void UpdateSelectionLabel()
    {
        var selected = _airports.Count(a => a.Selected && a.HasSuggestedChanges);
        var changedEnds = _airports.Where(a => a.Selected).SelectMany(a => a.Ends).Count(e => e.MarkingCode is 2 or 3);
        _selectionLabel.Text = selected == 0
            ? "尚未勾选机场；“勾选推荐项”会自动排除透明跑道"
            : $"已勾选 {selected} 个机场，预计转换 {changedEnds} 个跑道端";
        _applyButton.Enabled = !_busy && selected > 0;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _scanButton.Enabled = !busy;
        _locateButton.Enabled = !busy;
        _restoreButton.Enabled = !busy && BackupService.HasBackups(_aptPath.Text.Trim());
        foreach (var button in _browseButtons) button.Enabled = !busy;
        _recommendedButton.Enabled = !busy && _airports.Count > 0;
        _clearButton.Enabled = !busy && _airports.Count > 0;
        _airportList.Enabled = !busy;
        _searchBox.Enabled = !busy;
        _filterBox.Enabled = !busy;
        _aptPath.Enabled = !busy;
        _outputPath.Enabled = !busy;
        if (status is not null) _statusLabel.Text = status;
        if (!busy) _progress.Value = 0;
        UpdateSelectionLabel();
    }

    private void BrowseAptDat()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 X-Plane apt.dat",
            Filter = "X-Plane 机场数据库 (apt.dat)|apt.dat|所有文件 (*.*)|*.*",
            FileName = "apt.dat",
            CheckFileExists = true,
        };
        if (File.Exists(_aptPath.Text)) dialog.InitialDirectory = Path.GetDirectoryName(_aptPath.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _aptPath.Text = dialog.FileName;
            SaveSettings();
            _ = LoadOrScanAsync();
        }
    }

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安全导出目录；程序会在其中新建独立时间戳文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(_outputPath.Text) ? _outputPath.Text : "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputPath.Text = dialog.SelectedPath;
    }

    private void SortAirportList(int column)
    {
        if (_airportList.Items.Count < 2 || column < 0 || column >= _airportList.Columns.Count) return;
        PersistVisibleChecks();

        if (_sortColumn == column) _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        _airports.Sort((left, right) =>
        {
            var result = column switch
            {
                0 => left.Selected.CompareTo(right.Selected),
                1 => CompareText(left.IcaoCode, right.IcaoCode),
                2 => CompareText(left.Name, right.Name),
                3 => CompareText(left.RunwaySummary, right.RunwaySummary),
                4 => CompareText(left.CurrentSummary, right.CurrentSummary),
                5 => CompareText(left.SuggestedSummary, right.SuggestedSummary),
                6 => CompareText(left.Status, right.Status),
                _ => 0,
            };
            if (result == 0) result = CompareText(left.IcaoCode, right.IcaoCode);
            return _sortAscending ? result : -result;
        });

        RefreshAirportList(persistCurrentChecks: false);
        UpdateSortIndicator();
    }

    private void UpdateSortIndicator()
    {
        for (var i = 0; i < _airportList.Columns.Count; i++)
        {
            var title = _airportList.Columns[i].Text.Replace(" ▲", "").Replace(" ▼", "");
            _airportList.Columns[i].Text = i == _sortColumn
                ? $"{title} {(_sortAscending ? "▲" : "▼")}" : title;
        }
    }

    private static int CompareText(string left, string right) =>
        StringComparer.CurrentCultureIgnoreCase.Compare(left, right);

    private void ToggleHighlightedAirports()
    {
        var items = _airportList.SelectedItems.Cast<ListViewItem>()
            .Where(item => item.Tag is AirportRecord airport && airport.HasSuggestedChanges)
            .ToList();
        if (items.Count == 0) return;

        var newValue = items.Any(item => !item.Checked);
        _populating = true;
        try
        {
            foreach (var item in items)
            {
                item.Checked = newValue;
                if (item.Tag is AirportRecord airport) airport.Selected = newValue;
            }
        }
        finally
        {
            _populating = false;
        }
        UpdateSelectionLabel();
    }

    private void AirportListOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space) return;
        ToggleHighlightedAirports();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void ShowError(string title, Exception exception) =>
        MessageBox.Show(this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private Label PathLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = _navy,
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
    };

    private static void ConfigurePathBox(TextBox textBox, string placeholder)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.Margin = new Padding(0, 8, 10, 8);
        textBox.PlaceholderText = placeholder;
        textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    private Button SecondaryButton(string text)
    {
        var button = new Button { Text = text };
        StyleSecondaryButton(button);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2, 7, 0, 7);
        return button;
    }

    private void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = _accent;
        button.ForeColor = Color.White;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
    }

    private void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = _border;
        button.BackColor = Color.White;
        button.ForeColor = _navy;
        button.Font = new Font("Microsoft YaHei UI", 9F);
        button.Cursor = Cursors.Hand;
    }

    private static string FindDefaultOutputDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "中国机场跑道标线工具导出");

    private sealed class RoundedPanel : Panel
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Radius { get; set; } = 12;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get; set; } = Color.LightGray;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            var r = Radius * 2;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            path.AddArc(rect.Left, rect.Top, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Top, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawPath(pen, path);
        }
    }
}
