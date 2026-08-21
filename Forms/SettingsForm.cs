using System.Diagnostics;
using System.Net;
using LarkzeeChat.Models;
using LarkzeeChat.Networking;
using LarkzeeChat.Services;

namespace LarkzeeChat.Forms;

public sealed class SettingsForm : Form
{
    private readonly ChatSessionManager _sessionManager;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private readonly CheckBox _serverToggle;
    private readonly TextBox _localPasswordInput;
    private readonly Button _copyPasswordButton;
    private readonly TextBox _remoteIpInput;
    private readonly TextBox _remotePasswordInput;
    private readonly Button _saveButton;
    private bool _serverOperationInProgress;
    private bool _isInitializing;
    private bool _isClosing;

    public SettingsForm(
        ChatSessionManager sessionManager,
        SettingsService settingsService,
        AppSettings settings)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        Text = "配置 - Larkzee Chat";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 430);
        MinimumSize = new Size(460, 390);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Font;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18),
            BackColor = Color.White,
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        GroupBox serviceGroup = BuildServiceGroup(
            out _serverToggle,
            out _localPasswordInput,
            out _copyPasswordButton);
        GroupBox remoteGroup = BuildRemoteGroup(out _remoteIpInput, out _remotePasswordInput);
        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(450, 0),
            Text = "密码设置后会保存在本机，连接时由对方验证。首次启用时 Windows 可能提示防火墙访问，请仅允许专用网络。",
            ForeColor = Color.FromArgb(115, 115, 115),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic, GraphicsUnit.Point),
            Margin = new Padding(3, 7, 3, 7)
        };

        root.Controls.Add(serviceGroup, 0, 0);
        root.Controls.Add(remoteGroup, 0, 1);
        root.Controls.Add(hint, 0, 2);

        _saveButton = new Button
        {
            Text = "保存",
            AutoSize = true,
            MinimumSize = new Size(92, 30),
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.None
        };
        _saveButton.Click += SaveButton_Click;

        var buttons = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(18, 5, 18, 12)
        };
        buttons.Controls.Add(_saveButton);
        _saveButton.Left = buttons.ClientSize.Width - _saveButton.Width;
        _saveButton.Top = 2;
        buttons.Resize += (_, _) => _saveButton.Left = buttons.ClientSize.Width - _saveButton.Width;

        Controls.Add(root);
        Controls.Add(buttons);
        AcceptButton = _saveButton;

        _serverToggle.CheckedChanged += ServerToggle_CheckedChanged;
        _copyPasswordButton.Click += CopyPasswordButton_Click;
        _localPasswordInput.TextChanged += (_, _) => UpdateLocalPasswordControls();
        FormClosing += SettingsForm_FormClosing;
        FormClosed += (_, _) =>
        {
            _isClosing = true;
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        };

        _isInitializing = true;
        try
        {
            _localPasswordInput.Text = _settings.LocalPassword;
            _remoteIpInput.Text = _settings.RemoteIp;
            _remotePasswordInput.Text = _settings.RemotePassword;
            _serverToggle.Checked = _sessionManager.IsServerEnabled;
        }
        finally
        {
            _isInitializing = false;
        }

        UpdateLocalPasswordControls();
    }

    private static GroupBox BuildServiceGroup(
        out CheckBox toggle,
        out TextBox localPasswordInput,
        out Button copyPasswordButton)
    {
        var group = new GroupBox
        {
            Text = "本机连接服务",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 22, 12, 12),
            Margin = new Padding(0, 0, 0, 12)
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        toggle = new CheckBox
        {
            Text = "允许其他电脑连接",
            AutoSize = true,
            ThreeState = false,
            Margin = new Padding(3, 3, 3, 9),
            AccessibleName = "允许其他电脑连接"
        };

        var localPasswordLabel = new Label
        {
            Text = "本机连接密码",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 5, 10, 5),
            AccessibleName = "本机连接密码"
        };

        localPasswordInput = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = AuthenticationService.MaximumManualPasswordLength,
            UseSystemPasswordChar = true,
            Margin = new Padding(0, 2, 7, 5),
            AccessibleName = "本机连接密码"
        };

        copyPasswordButton = new Button
        {
            Text = "复制密码",
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 1, 0, 5),
            AccessibleName = "复制本机连接密码"
        };

        layout.Controls.Add(toggle, 0, 0);
        layout.SetColumnSpan(toggle, 3);
        layout.Controls.Add(localPasswordLabel, 0, 1);
        layout.Controls.Add(localPasswordInput, 1, 1);
        layout.Controls.Add(copyPasswordButton, 2, 1);

        group.Controls.Add(layout);
        return group;
    }

    private static GroupBox BuildRemoteGroup(out TextBox ipInput, out TextBox passwordInput)
    {
        var group = new GroupBox
        {
            Text = "连接其他电脑",
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 22, 12, 14),
            Margin = new Padding(0, 0, 0, 7)
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var ipLabel = new Label
        {
            Text = "对方 IP",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 5, 10, 5),
            AccessibleName = "对方 IP"
        };
        ipInput = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = 15,
            PlaceholderText = "例如 192.168.1.100",
            Margin = new Padding(0, 2, 0, 7),
            AccessibleName = "对方 IPv4 地址"
        };

        var passwordLabel = new Label
        {
            Text = "对方密码",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 5, 10, 5),
            AccessibleName = "对方密码"
        };
        passwordInput = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = AuthenticationService.MaximumManualPasswordLength,
            UseSystemPasswordChar = true,
            Margin = new Padding(0, 2, 0, 2),
            AccessibleName = "对方密码"
        };

        layout.Controls.Add(ipLabel, 0, 0);
        layout.Controls.Add(ipInput, 1, 0);
        layout.Controls.Add(passwordLabel, 0, 1);
        layout.Controls.Add(passwordInput, 1, 1);
        group.Controls.Add(layout);
        return group;
    }

    private async void ServerToggle_CheckedChanged(object? sender, EventArgs e)
    {
        if (_isInitializing || _isClosing || _serverOperationInProgress)
        {
            return;
        }

        await SetServerEnabledAsync(_serverToggle.Checked);
    }

    private async Task SetServerEnabledAsync(bool enabled)
    {
        _serverOperationInProgress = true;
        _serverToggle.Enabled = false;
        _copyPasswordButton.Enabled = false;
        _saveButton.Enabled = false;

        try
        {
            if (enabled)
            {
                if (!TryReadPendingSettings(requireLocalPassword: true, out PendingSettings pending))
                {
                    SetServerToggleState(false);
                    return;
                }

                // Persist the typed password before opening the listener. This
                // also keeps the remote connection fields in sync with what
                // the user sees in this form.
                if (!TryPersistPendingSettings(pending, out string persistError))
                {
                    SetServerToggleState(false);
                    ShowFriendlyError(persistError);
                    return;
                }

                ServerStartResult result = await _sessionManager
                    .EnableServerAsync(_lifetimeCts.Token)
                    .ConfigureAwait(true);
                if (!result.Succeeded)
                {
                    // The password remains saved and configured for a later
                    // retry; only the listener checkbox is reverted.
                    SetServerToggleState(false);
                    ShowFriendlyError("无法开启连接服务，请确认端口未被占用后重试。密码设置已保留。");
                }
            }
            else
            {
                await _sessionManager.DisableServerAsync(_lifetimeCts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // The settings window owns cancellation while it is closing.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat server toggle failed: {exception}");
            SetServerToggleState(!enabled);
            ShowFriendlyError(enabled
                ? "无法开启连接服务，请稍后重试。"
                : "无法关闭连接服务，请稍后重试。");
        }
        finally
        {
            _serverOperationInProgress = false;
            _serverToggle.Enabled = !_isClosing;
            _saveButton.Enabled = !_isClosing;
            UpdateLocalPasswordControls();
        }
    }

    private void CopyPasswordButton_Click(object? sender, EventArgs e)
    {
        string password = _localPasswordInput.Text;
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            Clipboard.SetText(password);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat could not copy the local password: {exception.Message}");
            ShowFriendlyError("复制密码失败，请手动记录密码。");
        }
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        if (_serverOperationInProgress || _isClosing)
        {
            return;
        }

        if (!TryReadPendingSettings(_serverToggle.Checked, out PendingSettings pending))
        {
            return;
        }

        if (!TryPersistPendingSettings(pending, out string persistError))
        {
            ShowFriendlyError(persistError);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryReadPendingSettings(bool requireLocalPassword, out PendingSettings pending)
    {
        pending = default;
        string localPassword = _localPasswordInput.Text;
        if (requireLocalPassword && string.IsNullOrEmpty(localPassword))
        {
            ShowFriendlyError("启用连接服务前，请先设置本机连接密码。");
            return false;
        }

        if (!string.IsNullOrEmpty(localPassword)
            && !AuthenticationService.TryValidateManualPassword(localPassword, out _))
        {
            ShowFriendlyError("本机连接密码需为 8–64 个字符，且首尾不能有空格。");
            return false;
        }

        string remoteIp = _remoteIpInput.Text.Trim();
        string remotePassword = _remotePasswordInput.Text;
        bool ipEmpty = string.IsNullOrEmpty(remoteIp);
        bool passwordEmpty = string.IsNullOrEmpty(remotePassword);
        if (ipEmpty != passwordEmpty)
        {
            ShowFriendlyError("请同时填写对方 IP 和对方密码。");
            return false;
        }

        string normalizedIp = string.Empty;
        if (!ipEmpty)
        {
            if (!Ipv4InputValidation.TryParseDottedDecimal(remoteIp, out IPAddress parsedAddress))
            {
                ShowFriendlyError("请输入合法的 IPv4 地址。");
                return false;
            }

            normalizedIp = parsedAddress.ToString();
        }

        if (!passwordEmpty
            && !AuthenticationService.TryValidateManualPassword(remotePassword, out _))
        {
            ShowFriendlyError("对方密码需为 8–64 个字符，且首尾不能有空格。");
            return false;
        }

        pending = new PendingSettings(normalizedIp, localPassword, remotePassword);
        return true;
    }

    private bool TryPersistPendingSettings(PendingSettings pending, out string error)
    {
        AppSettings previous = CloneSettings(_settings);
        AppSettings candidate = new()
        {
            RemoteIp = pending.RemoteIp,
            LocalPassword = pending.LocalPassword,
            RemotePassword = pending.RemotePassword
        };

        // Save first so a newly entered password is never left only in the
        // process if applying it to the manager is unavailable.
        if (!_settingsService.Save(candidate))
        {
            error = "配置保存失败，请检查本地文件权限。";
            return false;
        }

        bool passwordApplied = string.IsNullOrEmpty(pending.LocalPassword)
            ? _sessionManager.ClearConnectionPassword()
            : _sessionManager.SetConnectionPassword(pending.LocalPassword);
        if (!passwordApplied)
        {
            bool rolledBack = _settingsService.Save(previous);
            error = rolledBack
                ? "本机连接密码暂时无法应用，配置未更改。"
                : "本机连接密码暂时无法应用，且配置回滚失败，请检查本地文件。";
            return false;
        }

        _settings.RemoteIp = candidate.RemoteIp;
        _settings.LocalPassword = candidate.LocalPassword;
        _settings.RemotePassword = candidate.RemotePassword;
        error = string.Empty;
        return true;
    }

    private void SettingsForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_serverOperationInProgress)
        {
            e.Cancel = true;
            ShowFriendlyError("正在更新连接服务，请稍候。");
            return;
        }

        // Closing this window must not switch off a listener that the user enabled.
    }

    private void SetServerToggleState(bool enabled)
    {
        if (_serverToggle.Checked != enabled)
        {
            _serverToggle.Checked = enabled;
        }
    }

    private void UpdateLocalPasswordControls()
    {
        _copyPasswordButton.Enabled = !_serverOperationInProgress
            && !_isClosing
            && !string.IsNullOrEmpty(_localPasswordInput.Text);
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return new AppSettings
        {
            RemoteIp = settings.RemoteIp,
            LocalPassword = settings.LocalPassword,
            RemotePassword = settings.RemotePassword
        };
    }

    private static void ShowFriendlyError(string message)
    {
        MessageBox.Show(message, "Larkzee Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private readonly record struct PendingSettings(
        string RemoteIp,
        string LocalPassword,
        string RemotePassword);
}
