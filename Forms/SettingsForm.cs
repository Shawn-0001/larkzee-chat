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
    private readonly ComboBox _localAddressSelector;
    private readonly Label _localAddressStatusLabel;
    private readonly TextBox _localConnectionCodeInput;
    private readonly Button _copyConnectionCodeButton;
    private readonly TextBox _remoteConnectionCodeInput;
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
        ClientSize = new Size(500, 500);
        MinimumSize = new Size(460, 450);
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
            out _localAddressSelector,
            out _localAddressStatusLabel,
            out _localConnectionCodeInput,
            out _copyConnectionCodeButton);
        GroupBox remoteGroup = BuildRemoteGroup(
            out _remoteConnectionCodeInput,
            out _remoteIpInput,
            out _remotePasswordInput);
        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(450, 0),
            Text = "连接码包含局域网 IP 和临时短口令，连接建立后仍会使用加密通信。配置会保存在本机；首次启用时 Windows 可能提示防火墙访问，请仅允许专用网络。",
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
        _copyConnectionCodeButton.Click += CopyConnectionCodeButton_Click;
        _remoteConnectionCodeInput.TextChanged += RemoteConnectionCodeInput_TextChanged;
        Shown += (_, _) =>
        {
            RefreshLocalAddressCandidates();
            UpdateLocalConnectionControls();
        };
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
            RefreshLocalAddressCandidates();
            _remoteConnectionCodeInput.Text = _settings.RemoteConnectionCode ?? string.Empty;
            _remoteIpInput.Text = _settings.RemoteIp;
            _remotePasswordInput.Text = _settings.RemotePassword;
            _serverToggle.Checked = _sessionManager.IsServerEnabled;
        }
        finally
        {
            _isInitializing = false;
        }

        ApplyRemoteConnectionCodeToFields();
        UpdateLocalConnectionControls();
    }

    private static GroupBox BuildServiceGroup(
        out CheckBox toggle,
        out ComboBox localAddressSelector,
        out Label localAddressStatusLabel,
        out TextBox localConnectionCodeInput,
        out Button copyConnectionCodeButton)
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
            RowCount = 4,
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

        var localAddressLabel = new Label
        {
            Text = "本机 IP",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 5, 10, 5),
            AccessibleName = "本机局域网 IPv4 地址"
        };

        localAddressSelector = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false,
            MaxDropDownItems = 8,
            Margin = new Padding(0, 2, 0, 7),
            AccessibleName = "选择本机局域网 IPv4 地址"
        };

        var localCodeLabel = new Label
        {
            Text = "我的连接码",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 5, 10, 5),
            AccessibleName = "我的连接码"
        };

        localConnectionCodeInput = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            MaxLength = ConnectionCodeService.CodeLength,
            PlaceholderText = "开启服务后生成",
            BackColor = Color.FromArgb(248, 249, 251),
            Font = new Font("Consolas", 10F, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 2, 7, 5),
            AccessibleName = "我的连接码"
        };

        copyConnectionCodeButton = new Button
        {
            Text = "复制",
            AutoSize = true,
            FlatStyle = FlatStyle.System,
            Margin = new Padding(0, 1, 0, 5),
            AccessibleName = "复制我的连接码"
        };

        localAddressStatusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            ForeColor = Color.FromArgb(115, 115, 115),
            Margin = new Padding(3, 0, 3, 2),
            AccessibleName = "本机 IP 状态"
        };

        layout.Controls.Add(toggle, 0, 0);
        layout.SetColumnSpan(toggle, 3);
        layout.Controls.Add(localAddressLabel, 0, 1);
        layout.Controls.Add(localAddressSelector, 1, 1);
        layout.SetColumnSpan(localAddressSelector, 2);
        layout.Controls.Add(localCodeLabel, 0, 2);
        layout.Controls.Add(localConnectionCodeInput, 1, 2);
        layout.Controls.Add(copyConnectionCodeButton, 2, 2);
        layout.Controls.Add(localAddressStatusLabel, 0, 3);
        layout.SetColumnSpan(localAddressStatusLabel, 3);

        group.Controls.Add(layout);
        return group;
    }

    private static GroupBox BuildRemoteGroup(
        out TextBox connectionCodeInput,
        out TextBox ipInput,
        out TextBox passwordInput)
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
            RowCount = 4,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var codeLabel = new Label
        {
            Text = "对方连接码",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 5, 10, 5),
            AccessibleName = "对方连接码"
        };
        connectionCodeInput = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = ConnectionCodeService.CodeLength,
            CharacterCasing = CharacterCasing.Lower,
            PlaceholderText = "输入 8 位连接码",
            Margin = new Padding(0, 2, 0, 2),
            AccessibleName = "对方连接码"
        };

        var codeHint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(390, 0),
            Text = "输入连接码后自动填写；也可手动输入 IP 和密码。",
            ForeColor = Color.FromArgb(115, 115, 115),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Italic, GraphicsUnit.Point),
            Margin = new Padding(3, 0, 3, 7),
            AccessibleName = "连接码输入说明"
        };

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

        layout.Controls.Add(codeLabel, 0, 0);
        layout.Controls.Add(connectionCodeInput, 1, 0);
        layout.Controls.Add(codeHint, 0, 1);
        layout.SetColumnSpan(codeHint, 2);
        layout.Controls.Add(ipLabel, 0, 2);
        layout.Controls.Add(ipInput, 1, 2);
        layout.Controls.Add(passwordLabel, 0, 3);
        layout.Controls.Add(passwordInput, 1, 3);
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
        _localAddressSelector.Enabled = false;
        _copyConnectionCodeButton.Enabled = false;
        _saveButton.Enabled = false;

        AppSettings? previousSettings = null;
        string? previousCode = null;
        string? previousPassword = null;
        bool localConfigurationChanged = false;

        try
        {
            if (!enabled)
            {
                await _sessionManager.DisableServerAsync(_lifetimeCts.Token).ConfigureAwait(true);
                _localConnectionCodeInput.Clear();
                return;
            }

            if (!TryGetSelectedLocalAddress(out LocalNetworkAddressCandidate? localAddress)
                || !TryReadPendingSettings(_settings.LocalPassword, out PendingSettings remotePending))
            {
                SetServerToggleState(false);
                return;
            }

            if (localAddress is null)
            {
                SetServerToggleState(false);
                return;
            }

            ConnectionCodeInfo generated = ConnectionCodeService.Generate(localAddress.Address);
            previousSettings = CloneSettings(_settings);
            previousCode = _sessionManager.LocalConnectionCode;
            previousPassword = _sessionManager.LocalPassword;
            PendingSettings pending = remotePending with
            {
                LocalPassword = generated.AuthenticationPassword
            };

            if (!_sessionManager.SetConnectionCode(generated.Code))
            {
                SetServerToggleState(false);
                ShowFriendlyError("连接码生成失败，请重新尝试。配置未更改。");
                return;
            }
            localConfigurationChanged = true;

            if (!TryPersistPendingSettings(pending, out string persistError))
            {
                RestoreLocalConfiguration(previousCode, previousPassword);
                _settingsService.Save(previousSettings);
                CopySettings(previousSettings, _settings);
                SetServerToggleState(false);
                ShowFriendlyError(persistError);
                return;
            }

            ServerStartResult result = await _sessionManager
                .EnableServerAsync(_lifetimeCts.Token)
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                bool restored = RestoreLocalConfiguration(previousCode, previousPassword);
                bool settingsRolledBack = _settingsService.Save(previousSettings);
                CopySettings(previousSettings, _settings);
                _localConnectionCodeInput.Clear();
                SetServerToggleState(false);
                localConfigurationChanged = false;
                ShowFriendlyError(restored && settingsRolledBack
                    ? "无法开启连接服务，请确认端口未被占用后重试。配置未更改。"
                    : "无法开启连接服务，且配置回滚失败，请检查本地文件。请勿分享本次连接码。\n\n请关闭配置窗口后重试。"
                );
                return;
            }

            _localConnectionCodeInput.Text = generated.Code;
            localConfigurationChanged = false;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            if (localConfigurationChanged && previousSettings is not null)
            {
                RestoreLocalConfiguration(previousCode, previousPassword);
                _settingsService.Save(previousSettings);
                CopySettings(previousSettings, _settings);
            }

            // The settings window owns cancellation while it is closing.
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat server toggle failed: {exception}");
            if (localConfigurationChanged && previousSettings is not null)
            {
                RestoreLocalConfiguration(previousCode, previousPassword);
                _settingsService.Save(previousSettings);
                CopySettings(previousSettings, _settings);
                _localConnectionCodeInput.Clear();
            }

            SetServerToggleState(!enabled);
            ShowFriendlyError(enabled
                ? "无法开启连接服务，请稍后重试。配置未更改。"
                : "无法关闭连接服务，请稍后重试。");
        }
        finally
        {
            _serverOperationInProgress = false;
            _serverToggle.Enabled = !_isClosing;
            _saveButton.Enabled = !_isClosing;
            UpdateLocalConnectionControls();
        }
    }

    private void CopyConnectionCodeButton_Click(object? sender, EventArgs e)
    {
        string? code = _sessionManager.IsServerEnabled
            ? _sessionManager.LocalConnectionCode
            : null;
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        try
        {
            Clipboard.SetText(code);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat could not copy the local connection code: {exception.Message}");
            ShowFriendlyError("复制连接码失败，请手动记录连接码。");
        }
    }

    private void RemoteConnectionCodeInput_TextChanged(object? sender, EventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        ApplyRemoteConnectionCodeToFields();
    }

    private void ApplyRemoteConnectionCodeToFields()
    {
        string rawCode = _remoteConnectionCodeInput.Text.Trim();
        if (rawCode.Length != ConnectionCodeService.CodeLength
            || !ConnectionCodeService.TryDecode(
                rawCode,
                out ConnectionCodeInfo connectionCode,
                out _))
        {
            return;
        }

        if (_remoteConnectionCodeInput.Text != connectionCode.Code)
        {
            _remoteConnectionCodeInput.Text = connectionCode.Code;
            _remoteConnectionCodeInput.SelectionStart = _remoteConnectionCodeInput.TextLength;
        }

        _remoteIpInput.Text = connectionCode.Address.ToString();
        _remotePasswordInput.Text = connectionCode.AuthenticationPassword;
    }

    private void SaveButton_Click(object? sender, EventArgs e)
    {
        if (_serverOperationInProgress || _isClosing)
        {
            return;
        }

        if (!TryReadPendingSettings(_settings.LocalPassword, out PendingSettings pending))
        {
            return;
        }

        if (_serverToggle.Checked)
        {
            string? activeCode = _sessionManager.LocalConnectionCode;
            if (string.IsNullOrEmpty(activeCode)
                || !ConnectionCodeService.TryDecode(
                    activeCode,
                    out ConnectionCodeInfo localCode,
                    out _))
            {
                ShowFriendlyError("当前连接服务没有有效的连接码，请关闭服务后重新开启。");
                return;
            }

            pending = pending with { LocalPassword = localCode.AuthenticationPassword };
        }

        if (!TryPersistPendingSettings(pending, out string persistError))
        {
            ShowFriendlyError(persistError);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryReadPendingSettings(
        string localPassword,
        out PendingSettings pending)
    {
        pending = default;
        string rawCode = _remoteConnectionCodeInput.Text.Trim();
        if (!string.IsNullOrEmpty(rawCode))
        {
            if (!ConnectionCodeService.TryDecode(
                    rawCode,
                    out ConnectionCodeInfo connectionCode,
                    out ConnectionCodeFailureReason failureReason))
            {
                ShowFriendlyError(GetConnectionCodeError(failureReason));
                return false;
            }

            _remoteConnectionCodeInput.Text = connectionCode.Code;
            _remoteIpInput.Text = connectionCode.Address.ToString();
            _remotePasswordInput.Text = connectionCode.AuthenticationPassword;
            pending = new PendingSettings(
                connectionCode.Address.ToString(),
                localPassword,
                connectionCode.AuthenticationPassword,
                connectionCode.Code);
            return true;
        }

        string remoteIp = _remoteIpInput.Text.Trim();
        string remotePassword = _remotePasswordInput.Text;
        bool ipEmpty = string.IsNullOrEmpty(remoteIp);
        bool passwordEmpty = string.IsNullOrEmpty(remotePassword);
        if (ipEmpty != passwordEmpty)
        {
            ShowFriendlyError("请同时填写对方 IP 和对方密码，或输入对方连接码。");
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

        pending = new PendingSettings(
            normalizedIp,
            localPassword,
            remotePassword,
            string.Empty);
        return true;
    }

    private bool TryPersistPendingSettings(PendingSettings pending, out string error)
    {
        AppSettings candidate = new()
        {
            RemoteIp = pending.RemoteIp,
            LocalPassword = pending.LocalPassword,
            RemotePassword = pending.RemotePassword,
            RemoteConnectionCode = pending.RemoteConnectionCode
        };

        if (!_settingsService.Save(candidate))
        {
            error = "配置保存失败，请检查本地文件权限。";
            return false;
        }

        CopySettings(candidate, _settings);
        error = string.Empty;
        return true;
    }

    private void RefreshLocalAddressCandidates()
    {
        string? activeCode = _sessionManager.LocalConnectionCode;
        IPAddress? preferredAddress = null;
        if (!string.IsNullOrWhiteSpace(activeCode)
            && ConnectionCodeService.TryDecode(activeCode, out ConnectionCodeInfo activeConnectionCode, out _))
        {
            preferredAddress = activeConnectionCode.Address;
        }

        IReadOnlyList<LocalNetworkAddressCandidate> candidates;
        try
        {
            candidates = LocalNetworkAddressService.GetCandidates();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Larkzee Chat local network address discovery failed: {exception}");
            candidates = [];
        }

        _localAddressSelector.BeginUpdate();
        try
        {
            _localAddressSelector.Items.Clear();
            foreach (LocalNetworkAddressCandidate candidate in candidates)
            {
                _localAddressSelector.Items.Add(candidate);
            }

            int selectedIndex = -1;
            if (preferredAddress is not null)
            {
                for (int index = 0; index < candidates.Count; index++)
                {
                    if (candidates[index].Address.Equals(preferredAddress))
                    {
                        selectedIndex = index;
                        break;
                    }
                }
            }

            _localAddressSelector.SelectedIndex = selectedIndex >= 0
                ? selectedIndex
                : (candidates.Count > 0 ? 0 : -1);
        }
        finally
        {
            _localAddressSelector.EndUpdate();
        }

        _localAddressStatusLabel.Text = candidates.Count == 0
            ? "未发现可用的局域网 IPv4 地址（仅支持 10.x、172.16–31.x、192.168.x）。"
            : "连接码将包含当前选择的本机 IP；服务开启后不能切换。";
    }

    private bool TryGetSelectedLocalAddress(out LocalNetworkAddressCandidate? candidate)
    {
        candidate = _localAddressSelector.SelectedItem as LocalNetworkAddressCandidate;
        if (candidate is not null)
        {
            return true;
        }

        ShowFriendlyError("未找到可用的局域网 IPv4 地址，无法生成连接码。请连接到局域网后重试。\n\n支持的地址范围：10.x、172.16–31.x、192.168.x。");
        return false;
    }

    private bool RestoreLocalConfiguration(string? previousCode, string? previousPassword)
    {
        if (!string.IsNullOrWhiteSpace(previousCode)
            && _sessionManager.SetConnectionCode(previousCode))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(previousPassword)
            && _sessionManager.SetConnectionPassword(previousPassword))
        {
            return true;
        }

        return _sessionManager.ClearConnectionPassword();
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

    private void UpdateLocalConnectionControls()
    {
        bool serverEnabled = _sessionManager.IsServerEnabled;
        string? activeCode = _sessionManager.LocalConnectionCode;
        if (serverEnabled && !string.IsNullOrWhiteSpace(activeCode))
        {
            _localConnectionCodeInput.Text = activeCode;
        }
        else if (!serverEnabled)
        {
            _localConnectionCodeInput.Clear();
        }

        _localAddressSelector.Enabled = !_serverOperationInProgress
            && !_isClosing
            && !serverEnabled;
        _copyConnectionCodeButton.Enabled = !_serverOperationInProgress
            && !_isClosing
            && serverEnabled
            && !string.IsNullOrEmpty(activeCode);
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return new AppSettings
        {
            RemoteIp = settings.RemoteIp,
            LocalPassword = settings.LocalPassword,
            RemotePassword = settings.RemotePassword,
            RemoteConnectionCode = settings.RemoteConnectionCode
        };
    }

    private static void CopySettings(AppSettings source, AppSettings target)
    {
        target.RemoteIp = source.RemoteIp;
        target.LocalPassword = source.LocalPassword;
        target.RemotePassword = source.RemotePassword;
        target.RemoteConnectionCode = source.RemoteConnectionCode;
    }

    private static string GetConnectionCodeError(ConnectionCodeFailureReason failureReason)
    {
        return failureReason switch
        {
            ConnectionCodeFailureReason.InvalidLength => "连接码需为 8 个字符，请检查是否完整。",
            ConnectionCodeFailureReason.InvalidCharacter => "连接码只能使用小写字母和数字（不含 0、1、i、o）。",
            ConnectionCodeFailureReason.ChecksumMismatch => "连接码输入有误，请检查字符。",
            ConnectionCodeFailureReason.InvalidPayload => "连接码无法识别，请确认对方提供的是最新连接码。",
            ConnectionCodeFailureReason.UnsupportedAddress => "连接码中的地址不受支持，请让对方重新生成连接码。",
            _ => "连接码无效，请检查后重试。"
        };
    }

    private static void ShowFriendlyError(string message)
    {
        MessageBox.Show(message, "Larkzee Chat", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private readonly record struct PendingSettings(
        string RemoteIp,
        string LocalPassword,
        string RemotePassword,
        string RemoteConnectionCode);
}
