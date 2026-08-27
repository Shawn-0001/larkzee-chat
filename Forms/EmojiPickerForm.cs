using System.Diagnostics;
using LarkzeeChat.Models;
using LarkzeeChat.Services;

namespace LarkzeeChat.Forms;

public sealed class EmojiPickerForm : Form
{
    private static readonly string[] Emojis =
    [
        "😀", "😄", "😁", "😂", "😊", "🙂", "😉", "😍",
        "🥰", "😘", "😎", "🤔", "😅", "🥲", "😢", "😭",
        "😤", "😡", "🤯", "😴", "🤗", "🤝", "👍", "👎",
        "👏", "🙏", "💪", "👌", "✌️", "❤️", "💯", "🎉",
        "🔥", "✨", "⭐", "✅", "❌", "⚠️", "📌", "📎"
    ];

    private readonly EmojiPackService _emojiPackService;
    private readonly ComboBox _packSelector;
    private readonly FlowLayoutPanel _stickerGrid;
    private readonly Label _packStatus;
    private readonly Button _deletePackButton;
    private readonly Button _importPackButton;
    private readonly Button _exportPackButton;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IReadOnlyList<EmojiPack> _packs = Array.Empty<EmojiPack>();
    private CancellationTokenSource? _renderCts;
    private bool _suppressSelectionChanged;
    private bool _suppressDeactivate;
    private bool _operationBusy;

    public EmojiPickerForm(EmojiPackService? emojiPackService = null)
    {
        _emojiPackService = emojiPackService ?? new EmojiPackService();

        Text = "选择表情";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(456, 372);
        MinimumSize = new Size(420, 340);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;
        Deactivate += Picker_Deactivate;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 5),
            AccessibleName = "表情选择区域"
        };
        TabPage unicodePage = BuildUnicodePage();
        TabPage packPage = BuildPackPage(
            out _packSelector,
            out _stickerGrid,
            out _packStatus,
            out _importPackButton,
            out _exportPackButton,
            out _deletePackButton);
        tabs.TabPages.Add(unicodePage);
        tabs.TabPages.Add(packPage);
        Controls.Add(tabs);

        _packSelector.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressSelectionChanged)
            {
                _ = RenderSelectedPackAsync();
            }
        };
        _importPackButton.Click += (_, _) => _ = ImportPackFromDialogAsync();
        _exportPackButton.Click += (_, _) => _ = ExportPackFromDialogAsync();
        _deletePackButton.Click += (_, _) => _ = DeleteCurrentPackAsync();
        Shown += (_, _) => _ = RefreshPacksAsync();
    }

    public event EventHandler? SelectionMade;

    public string? SelectedEmoji { get; private set; }

    public string? SelectedStickerPath { get; private set; }

    private TabPage BuildUnicodePage()
    {
        var page = new TabPage("常用表情")
        {
            BackColor = Color.White,
            Padding = new Padding(6),
            AccessibleName = "常用表情"
        };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 5,
            Padding = new Padding(2),
            BackColor = Color.White,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
            AccessibleName = "常用表情网格"
        };
        for (int column = 0; column < grid.ColumnCount; column++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5F));
        }

        for (int row = 0; row < grid.RowCount; row++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
        }

        foreach (string emoji in Emojis)
        {
            var button = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(2),
                Text = emoji,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(24, 34, 48),
                Font = new Font("Segoe UI Emoji", 14F, FontStyle.Regular, GraphicsUnit.Point),
                TabStop = true,
                AccessibleName = $"插入表情 {emoji}"
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 244, 248);
            button.Click += (_, _) => SelectEmoji(emoji);
            grid.Controls.Add(button);
        }

        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildPackPage(
        out ComboBox packSelector,
        out FlowLayoutPanel stickerGrid,
        out Label packStatus,
        out Button importPackButton,
        out Button exportPackButton,
        out Button deletePackButton)
    {
        var page = new TabPage("我的表情")
        {
            BackColor = Color.White,
            Padding = new Padding(8),
            AccessibleName = "我的表情"
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.White,
            AccessibleName = "表情包管理"
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var management = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.White,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 4)
        };
        management.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        management.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        management.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        management.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        packSelector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "选择表情包",
            Margin = new Padding(0, 0, 5, 3)
        };
        importPackButton = BuildManagementButton("导入表情", "导入表情包文件夹");
        exportPackButton = BuildManagementButton("导出表情", "导出当前表情包");
        deletePackButton = BuildManagementButton("删除表情", "删除当前表情包");

        management.Controls.Add(packSelector, 0, 0);
        management.Controls.Add(importPackButton, 1, 0);
        management.Controls.Add(exportPackButton, 2, 0);
        management.Controls.Add(deletePackButton, 3, 0);

        stickerGrid = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.FromArgb(247, 248, 250),
            Padding = new Padding(6),
            Margin = new Padding(0),
            AccessibleName = "自定义表情网格"
        };
        packStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(98, 109, 124),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            AccessibleName = "表情包操作状态",
            Padding = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(management, 0, 0);
        layout.Controls.Add(stickerGrid, 0, 1);
        layout.Controls.Add(packStatus, 0, 2);
        page.Controls.Add(layout);
        deletePackButton.Enabled = false;
        exportPackButton.Enabled = false;
        return page;
    }

    private static Button BuildManagementButton(string text, string accessibleName)
    {
        var button = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(68, 79, 96),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            AccessibleName = accessibleName,
            AccessibleRole = AccessibleRole.PushButton,
            TabStop = true,
            Margin = new Padding(2, 1, 2, 1)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(220, 225, 232);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 241, 252);
        return button;
    }

    private void SelectEmoji(string emoji)
    {
        SelectedEmoji = emoji;
        SelectedStickerPath = null;
        SelectionMade?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void SelectSticker(EmojiPack pack, EmojiSticker sticker)
    {
        if (!_emojiPackService.TryGetStickerPath(pack.Id, sticker.Id, out string path))
        {
            _packStatus.Text = "表情文件不存在，请重新导入表情包。";
            return;
        }

        SelectedStickerPath = path;
        SelectedEmoji = null;
        SelectionMade?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private async Task RefreshPacksAsync(string? status = null)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        _packStatus.Text = "正在读取本地缓存…";
        IReadOnlyList<EmojiPack> packs = await Task.Run(
            () => _emojiPackService.Refresh(),
            _lifetimeCts.Token).ConfigureAwait(true);
        if (IsDisposed || Disposing)
        {
            return;
        }

        string? previousId = (_packSelector.SelectedItem as PackOption)?.Id;
        _suppressSelectionChanged = true;
        _packSelector.BeginUpdate();
        try
        {
            _packs = packs;
            _packSelector.Items.Clear();
            foreach (EmojiPack pack in packs)
            {
                _packSelector.Items.Add(new PackOption(pack.Id, pack.Name));
            }

            int selected = previousId is null
                ? 0
                : _packSelector.Items.Cast<PackOption>().ToList().FindIndex(option =>
                    string.Equals(option.Id, previousId, StringComparison.OrdinalIgnoreCase));
            _packSelector.SelectedIndex = _packSelector.Items.Count == 0
                ? -1
                : selected >= 0 ? selected : 0;
        }
        finally
        {
            _packSelector.EndUpdate();
            _suppressSelectionChanged = false;
        }

        await RenderSelectedPackAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(status))
        {
            _packStatus.Text = status;
        }
    }

    private async Task RenderSelectedPackAsync()
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancellationToken cancellationToken = _renderCts.Token;
        ClearStickerGrid();

        EmojiPack? pack = GetSelectedPack();
        _deletePackButton.Enabled = pack is not null;
        _exportPackButton.Enabled = pack is not null;
        if (pack is null)
        {
            _packStatus.Text = "还没有表情包，点击“导入表情”添加 PNG/GIF。";
            return;
        }

        if (pack.Stickers.Count == 0)
        {
            _packStatus.Text = $"{pack.Name} 为空，请导入 PNG/GIF 文件。";
            return;
        }

        _packStatus.Text = $"正在加载 {pack.Name} 的缓存…";
        List<LoadedSticker> loaded = await Task.Run(
            () => LoadThumbnails(pack, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        if (cancellationToken.IsCancellationRequested || IsDisposed || Disposing)
        {
            foreach (LoadedSticker item in loaded)
            {
                item.Thumbnail?.Dispose();
            }

            return;
        }

        foreach (LoadedSticker item in loaded)
        {
            if (item.Thumbnail is null)
            {
                continue;
            }

            _stickerGrid.Controls.Add(BuildStickerButton(pack, item.Sticker, item.Thumbnail));
        }

        _packStatus.Text = $"{pack.Name} · {loaded.Count} 个表情（本地缓存）";
    }

    private List<LoadedSticker> LoadThumbnails(
        EmojiPack pack,
        CancellationToken cancellationToken)
    {
        List<LoadedSticker> loaded = [];
        foreach (EmojiSticker sticker in pack.Stickers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryLoadStickerThumbnail(pack, sticker, out Image? thumbnail))
            {
                continue;
            }

            loaded.Add(new LoadedSticker(sticker, thumbnail));
        }

        return loaded;
    }

    private bool TryLoadStickerThumbnail(
        EmojiPack pack,
        EmojiSticker sticker,
        out Image? thumbnail)
    {
        thumbnail = null;
        if (!_emojiPackService.TryGetStickerPath(pack.Id, sticker.Id, out string path))
        {
            return false;
        }

        try
        {
            thumbnail = LoadThumbnailFromPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or OutOfMemoryException)
        {
            Debug.WriteLine(exception);
            return false;
        }
    }

    private Button BuildStickerButton(EmojiPack pack, EmojiSticker sticker, Image thumbnail)
    {
        var button = new Button
        {
            Size = new Size(76, 76),
            Margin = new Padding(3),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            AccessibleName = $"发送表情 {sticker.DisplayName}",
            AccessibleRole = AccessibleRole.PushButton,
            TabStop = true,
            Tag = sticker.Id,
            Image = thumbnail,
            ImageAlign = ContentAlignment.MiddleCenter
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(225, 229, 234);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 244, 248);
        button.Disposed += (_, _) => thumbnail.Dispose();
        button.Click += (_, _) => SelectSticker(pack, sticker);
        return button;
    }

    private static Image LoadThumbnailFromPath(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using Image source = Image.FromStream(stream, false, true);
            var thumbnail = new Bitmap(58, 58);
            using Graphics graphics = Graphics.FromImage(thumbnail);
            graphics.Clear(Color.White);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            Rectangle target = FitInside(source.Size, thumbnail.Size);
            graphics.DrawImage(source, target);
            return thumbnail;
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static Rectangle FitInside(Size source, Size bounds)
    {
        double scale = Math.Min(
            bounds.Width / (double)Math.Max(1, source.Width),
            bounds.Height / (double)Math.Max(1, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
    }

    private EmojiPack? GetSelectedPack()
    {
        string? selectedId = (_packSelector.SelectedItem as PackOption)?.Id;
        return selectedId is null
            ? null
            : _packs.FirstOrDefault(pack => string.Equals(pack.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ImportPackFromDialogAsync()
    {
        if (_operationBusy) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要导入的表情包文件夹（顶层 PNG/GIF）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        _suppressDeactivate = true;
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
        }
        finally { _suppressDeactivate = false; }

        _operationBusy = true;
        SetManagementEnabled(false);
        _packStatus.Text = "正在复制表情到本地缓存…聊天窗口不会被阻塞。";
        try
        {
            EmojiPackImportResult result = await Task.Run(
                () => _emojiPackService.ImportFolder(dialog.SelectedPath), _lifetimeCts.Token);
            string message = result.ImportedStickers.Count > 0 && result.RejectedFiles.Count == 0
                ? $"已导入 {result.ImportedStickers.Count} 个表情到本地缓存。"
                : result.ImportedStickers.Count > 0
                    ? $"已导入 {result.ImportedStickers.Count} 个表情，拒绝 {result.RejectedFiles.Count} 个。"
                    : result.RejectedFiles.Count > 0
                        ? $"没有导入成功，拒绝 {result.RejectedFiles.Count} 个文件。"
                        : "没有找到可导入的 PNG/GIF 文件。";
            await RefreshPacksAsync(message);
            MessageBox.Show(message, "表情包", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        finally { _operationBusy = false; SetManagementEnabled(true); }
    }

    private async Task ExportPackFromDialogAsync()
    {
        EmojiPack? pack = GetSelectedPack();
        if (_operationBusy || pack is null) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择导出位置，程序会创建表情包文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        _suppressDeactivate = true;
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
        }
        finally { _suppressDeactivate = false; }

        _operationBusy = true;
        SetManagementEnabled(false);
        _packStatus.Text = "正在从本地缓存导出表情…";
        try
        {
            int exported = await Task.Run(
                () => _emojiPackService.ExportPack(pack.Id, dialog.SelectedPath), _lifetimeCts.Token);
            string message = exported > 0 ? $"已导出 {exported} 个表情。" : "没有可导出的缓存表情。";
            _packStatus.Text = message;
            MessageBox.Show(message, "导出表情", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine(exception);
            _packStatus.Text = "导出失败，请选择其他目录。";
        }
        finally { _operationBusy = false; SetManagementEnabled(true); }
    }

    private async Task DeleteCurrentPackAsync()
    {
        EmojiPack? pack = GetSelectedPack();
        if (_operationBusy || pack is null) return;
        _suppressDeactivate = true;
        try
        {
            DialogResult confirmation = MessageBox.Show(
                $"确定删除表情包“{pack.Name}”吗？其中的本地缓存也会删除。",
                "删除表情包", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes) return;
        }
        finally { _suppressDeactivate = false; }

        _operationBusy = true;
        SetManagementEnabled(false);
        try
        {
            bool deleted = await Task.Run(
                () => _emojiPackService.DeletePack(pack.Id), _lifetimeCts.Token);
            await RefreshPacksAsync(deleted ? "表情包缓存已删除。" : "表情包删除失败。");
        }
        finally { _operationBusy = false; SetManagementEnabled(true); }
    }

    private void SetManagementEnabled(bool enabled)
    {
        _packSelector.Enabled = enabled;
        _importPackButton.Enabled = enabled;
        _exportPackButton.Enabled = enabled && GetSelectedPack() is not null;
        _deletePackButton.Enabled = enabled && GetSelectedPack() is not null;
    }

    private void ClearStickerGrid()
    {
        foreach (Control control in _stickerGrid.Controls.Cast<Control>().ToArray())
        {
            _stickerGrid.Controls.Remove(control);
            control.Dispose();
        }
    }

    private void Picker_Deactivate(object? sender, EventArgs e)
    {
        if (_suppressDeactivate || IsDisposed || Disposing) return;
        BeginInvoke(new Action(() =>
        {
            if (!_suppressDeactivate && !IsDisposed && !Disposing) Close();
        }));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        base.OnFormClosed(e);
    }

    private sealed record PackOption(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record LoadedSticker(EmojiSticker Sticker, Image? Thumbnail);
}
