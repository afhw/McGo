using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Authentication;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.WinUI.Dialogs;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace McGo.WinUI.Pages;

public sealed partial class AccountsPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly MicrosoftAuthenticator _microsoftAuthenticator = new(AppConstants.ClientId, AppConstants.RedirectUri);
    private readonly OAuthCallbackServer _oauthCallbackServer = new();
    private List<JsonObject> _accounts = [];
    private readonly Dictionary<string, JsonObject> _accountsByLabel = [];
    private string _lastMicrosoftLoginUrl = "";
    private bool _initialized;

    public AccountsPage()
    {
        InitializeComponent();
        _initialized = true;
        LoginModeBox.SelectedItem = LoginModeBox.Items[0];
        Refresh();
    }

    private void RefreshAccounts(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh(string? selectedAccountId = null)
    {
        _settings.LoadConfig();
        _accounts = _settings.LoadAccounts();
        selectedAccountId ??= _settings.Config.Get("ACCOUNTS", "selected_account_id", "");
        AutoOpenBrowserCheckBox.IsChecked = _settings.Config.GetBool("AUTH", "auto_open_browser", true);
        ApplyAdvancedVisibility();
        _accountsByLabel.Clear();
        var labels = new List<string>();
        foreach (var account in _accounts)
        {
            var label = AccountLabel(account);
            labels.Add(label);
            _accountsByLabel[label] = account;
        }

        CurrentAccountBox.ItemsSource = labels;
        DeleteAccountBox.ItemsSource = labels;
        if (!string.IsNullOrWhiteSpace(selectedAccountId))
        {
            CurrentAccountBox.SelectedItem = labels.FirstOrDefault(label => _accountsByLabel[label]["id"]?.GetValue<string>() == selectedAccountId);
        }
        if (CurrentAccountBox.SelectedItem is null && labels.Count > 0)
        {
            CurrentAccountBox.SelectedIndex = 0;
        }

        DeleteAccountBox.SelectedItem = CurrentAccountBox.SelectedItem;

        OutputBox.Text = labels.Count == 0 ? "当前没有账号。" : $"已加载 {labels.Count} 个账号。";
        EmptyAccountsCard.Visibility = labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (labels.Count == 0)
        {
            _settings.Config.Set("ACCOUNTS", "selected_account_id", "");
            _settings.SaveConfig();
            CurrentAccountStatusText.Text = "当前账号：未选择";
        }
    }

    private void CurrentAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAccount() is not { } account)
        {
            return;
        }

        var type = account["type"]?.GetValue<string>() ?? "offline";
        _settings.Config.Set("ACCOUNTS", "selected_account_id", account["id"]?.GetValue<string>() ?? "");
        _settings.SaveConfig();
        CurrentAccountStatusText.Text = "当前账号：" + AccountLabel(account);

        if (type == "offline")
        {
            OfflineNameBox.Text = account["username"]?.GetValue<string>() ?? account["display_name"]?.GetValue<string>() ?? "";
            OfflineUuidBox.Text = account["uuid"]?.GetValue<string>() ?? "";
            OfflineAccessTokenBox.Password = account["access_token"]?.GetValue<string>() ?? "";
        }
        else if (type == "external")
        {
            ExternalServerBox.Text = account["auth_server"]?.GetValue<string>() ?? account["server"]?.GetValue<string>() ?? "";
            ExternalUsernameBox.Text = account["username"]?.GetValue<string>() ?? account["display_name"]?.GetValue<string>() ?? "";
            AuthlibPathBox.Text = account["authlib_injector_path"]?.GetValue<string>() ?? "";
            ExternalStatusText.Text = $"当前外置账号：{AccountLabel(account)}";
        }
    }

    private void LoginModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var mode = SelectedLoginMode();
        var showEditor = mode != "overview";
        CurrentAccountCard.Visibility = showEditor ? Visibility.Collapsed : Visibility.Visible;
        AccountEditors.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        OfflineAccountCard.Visibility = mode == "offline" ? Visibility.Visible : Visibility.Collapsed;
        MicrosoftAccountCard.Visibility = mode == "microsoft" ? Visibility.Visible : Visibility.Collapsed;
        ExternalAccountCard.Visibility = mode == "external" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "microsoft")
        {
            FocusMicrosoftLogin(sender, new RoutedEventArgs());
        }
        else if (mode == "external")
        {
            FocusExternalLogin(sender, new RoutedEventArgs());
        }
        else if (mode == "offline")
        {
            FocusOfflineAccount(sender, new RoutedEventArgs());
        }
        else
        {
            FocusCurrentAccounts(sender, new RoutedEventArgs());
        }
    }

    private void DeleteAccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    /// <summary>从空状态直接进离线登录并聚焦用户名，减少一次选择成本。</summary>
    private void StartOfflineAccountFlow(object sender, RoutedEventArgs e)
    {
        SelectLoginMode("offline");
        OfflineNameBox.Focus(FocusState.Programmatic);
    }

    private void SelectLoginMode(string mode)
    {
        foreach (var item in LoginModeBox.Items.OfType<SelectorBarItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                LoginModeBox.SelectedItem = item;
                return;
            }
        }
    }

    private void FocusCurrentAccounts(object sender, RoutedEventArgs e)
    {
        CurrentAccountBox.Focus(FocusState.Programmatic);
        OutputBox.Text = "已切到当前账号列表。";
    }

    private void FocusOfflineAccount(object sender, RoutedEventArgs e)
    {
        OfflineNameBox.Focus(FocusState.Programmatic);
        OutputBox.Text = "填写用户名后可添加或更新离线账号。";
    }

    private void FocusMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        AutoOpenBrowserCheckBox.Focus(FocusState.Programmatic);
        OutputBox.Text = "可生成 Microsoft 登录链接，或直接添加 Microsoft 账号。";
    }

    private void FocusExternalLogin(object sender, RoutedEventArgs e)
    {
        ExternalServerBox.Focus(FocusState.Programmatic);
        UpdateExternalStatus();
        OutputBox.Text = "填写外置登录服务器、用户名、密码和 authlib-injector.jar 后登录。";
    }

    private async void RefreshSelectedAccount(object sender, RoutedEventArgs e)
    {
        var account = SelectedAccount();
        if (account is null)
        {
            OutputBox.Text = "请先选择账号。";
            return;
        }

        try
        {
            var type = account["type"]?.GetValue<string>() ?? "offline";
            if (type == "microsoft")
            {
                OutputBox.Text = "正在刷新 Microsoft 账号...";
                var refreshToken = account["refresh_token"]?.GetValue<string>() ?? "";
                var authenticator = new MicrosoftAuthenticator(AppConstants.ClientId, AppConstants.RedirectUri);
                await authenticator.RefreshAccessTokenAsync(refreshToken);
                var profile = await authenticator.GetMinecraftProfileAsync();
                account["display_name"] = profile.Name;
                account["username"] = profile.Name;
                account["uuid"] = profile.Id;
                account["access_token"] = authenticator.MinecraftAccessToken ?? "";
                account["refresh_token"] = authenticator.RefreshToken ?? refreshToken;
                UpsertAccount(account);
                OutputBox.Text = $"Microsoft 账号已刷新：{profile.Name}";
                return;
            }

            if (type == "external")
            {
                OutputBox.Text = "正在刷新外置账号...";
                account["auth_server"] = ExternalAuthService.NormalizeAuthServer(ExternalServerBox.Text.Trim().Length > 0 ? ExternalServerBox.Text : account["auth_server"]?.GetValue<string>() ?? "");
                account["authlib_injector_path"] = AuthlibPathBox.Text.Trim().Length > 0 ? AuthlibPathBox.Text.Trim() : account["authlib_injector_path"]?.GetValue<string>() ?? "";
                var service = new ExternalAuthService();
                var refreshed = await service.RefreshExternalAccountAsync(account);
                refreshed["id"] = account["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                refreshed["type"] = "external";
                refreshed["auth_server"] = account["auth_server"]?.GetValue<string>() ?? refreshed["auth_server"]?.GetValue<string>() ?? "";
                refreshed["authlib_injector_path"] = account["authlib_injector_path"]?.GetValue<string>() ?? "";
                UpsertAccount(refreshed);
                ExternalStatusText.Text = "外置账号已刷新/验证。";
                OutputBox.Text = "外置账号已刷新/验证。";
                return;
            }

            OutputBox.Text = "离线账号无需刷新。";
        }
        catch (Exception exc)
        {
            OutputBox.Text = "刷新账号失败：" + exc.Message;
        }
    }

    private void AddOfflineAccount(object sender, RoutedEventArgs e)
    {
        var username = OfflineNameBox.Text.Trim();
        if (username.Length == 0)
        {
            OutputBox.Text = "离线账号需要用户名。";
            return;
        }

        var existing = _accounts.FirstOrDefault(account => account["type"]?.GetValue<string>() == "offline" && account["username"]?.GetValue<string>() == username);
        var account = existing ?? new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = "offline" };
        account["display_name"] = username;
        account["username"] = username;
        account["uuid"] = OfflineUuidBox.Text.Trim();
        account["access_token"] = OfflineAccessTokenBox.Password.Trim();
        account["refresh_token"] = "";
        if (existing is null)
        {
            _accounts.Add(account);
        }

        _settings.SaveAccounts(_accounts);
        Refresh(account["id"]?.GetValue<string>());
        OutputBox.Text = "离线账号已保存。";
    }

    private async void DeleteSelectedAccount(object sender, RoutedEventArgs e)
    {
        var account = SelectedDeleteAccount();
        if (account is null)
        {
            OutputBox.Text = "请先选择账号。";
            return;
        }

        var dialog = AppDialog.Create(this, "删除账号", $"确定要删除 {AccountLabel(account)} 吗？", "删除", "取消");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var id = account["id"]?.GetValue<string>() ?? "";
        _accounts = _accounts.Where(item => item["id"]?.GetValue<string>() != id).ToList();
        _settings.SaveAccounts(_accounts);
        Refresh();
        OutputBox.Text = "账号已删除。";
    }

    private void NormalizeExternalServer(object sender, RoutedEventArgs e)
    {
        try
        {
            ExternalServerBox.Text = ExternalAuthService.NormalizeAuthServer(ExternalServerBox.Text);
            UpdateExternalStatus();
            OutputBox.Text = "认证服务器地址已规范化。";
        }
        catch (Exception exc)
        {
            ExternalStatusText.Text = "服务器地址无效：" + exc.Message;
            OutputBox.Text = exc.Message;
        }
    }

    private async void ProbeExternalServer(object sender, RoutedEventArgs e)
    {
        try
        {
            ExternalStatusText.Text = "正在探测认证服务器...";
            OutputBox.Text = "正在探测认证服务器...";
            var service = new ExternalAuthService();
            var result = await service.ProbeExternalAuthServerAsync(ExternalServerBox.Text);
            ExternalServerBox.Text = result.Server;
            ExternalStatusText.Text = result.Message;
            OutputBox.Text = result.Message;
        }
        catch (Exception exc)
        {
            ExternalStatusText.Text = "认证服务器探测失败：" + exc.Message;
            OutputBox.Text = "认证服务器探测失败：" + exc.Message;
        }
    }

    private void DownloadAuthlibInjector(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.LoadConfig();
            var gameDir = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
            var targetDir = Path.Combine(gameDir, "authlib-injector");
            ExternalStatusText.Text = "已加入全局队列，等待下载 authlib-injector...";
            OutputBox.Text = ExternalStatusText.Text;
            DownloadTaskManager.Current.Enqueue("authlib", "下载 authlib-injector", (record, token) =>
                DownloadAuthlibInjectorTaskAsync(record, targetDir, token));
        }
        catch (Exception exc)
        {
            ExternalStatusText.Text = "authlib-injector 下载失败：" + exc.Message;
            OutputBox.Text = "authlib-injector 下载失败：" + exc.Message;
        }
    }

    private async Task<string> DownloadAuthlibInjectorTaskAsync(
        DownloadTaskRecord record,
        string targetDir,
        CancellationToken cancellationToken)
    {
        try
        {
            ExternalStatusText.Text = "正在下载 authlib-injector...";
            OutputBox.Text = ExternalStatusText.Text;
            DownloadTaskManager.Current.Update(record, item => item.Detail = "正在下载 authlib-injector...");
            var service = new ExternalAuthService();
            var result = await service.DownloadAuthlibInjectorAsync(targetDir, cancellationToken);
            AuthlibPathBox.Text = result.Path;
            UpdateExternalStatus();
            OutputBox.Text = "authlib-injector 已下载：" + result.Path;
            return "authlib-injector 已下载";
        }
        catch (OperationCanceledException)
        {
            ExternalStatusText.Text = "authlib-injector 下载已取消。";
            OutputBox.Text = ExternalStatusText.Text;
            throw;
        }
        catch (Exception exc)
        {
            ExternalStatusText.Text = "authlib-injector 下载失败：" + exc.Message;
            OutputBox.Text = "authlib-injector 下载失败：" + exc.Message;
            throw;
        }
    }

    private async void ChooseAuthlibInjector(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        picker.FileTypeFilter.Add(".jar");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            AuthlibPathBox.Text = file.Path;
            UpdateExternalStatus();
            OutputBox.Text = "已选择 authlib-injector.jar：" + file.Path;
        }
    }

    private async void StartMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        try
        {
            GenerateMicrosoftLogin(sender, e);
            SaveMicrosoftLoginSettings();
            if (AutoOpenBrowserCheckBox.IsChecked == true)
            {
                Process.Start(new ProcessStartInfo(_lastMicrosoftLoginUrl) { UseShellExecute = true });
                MicrosoftOutputBox.Text = LoginHelpText(_lastMicrosoftLoginUrl, "已打开浏览器。完成授权后保持此窗口等待回调。");
            }
            else
            {
                MicrosoftOutputBox.Text = LoginHelpText(_lastMicrosoftLoginUrl, "浏览器自动打开已关闭。请复制链接到外部浏览器完成授权。");
            }

            await WaitMicrosoftCallbackAsync();
        }
        catch (Exception exc)
        {
            MicrosoftOutputBox.Text = exc.Message;
            OutputBox.Text = "Microsoft 登录失败。";
        }
    }

    private void GenerateMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        _lastMicrosoftLoginUrl = _microsoftAuthenticator.GetLoginUrl();
        MicrosoftLoginLinkBox.Text = _lastMicrosoftLoginUrl;
        SaveMicrosoftLoginSettings();
        MicrosoftOutputBox.Text = LoginHelpText(_lastMicrosoftLoginUrl, AutoOpenBrowserCheckBox.IsChecked == true
            ? "可点击“开始登录”自动打开浏览器，也可以手动复制链接。"
            : "自动打开浏览器已关闭，请复制或手动打开下面的链接。");
        OutputBox.Text = "Microsoft 登录链接已生成。";
    }

    private void CopyMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        var loginUrl = CurrentMicrosoftLoginUrl();
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            GenerateMicrosoftLogin(sender, e);
            loginUrl = CurrentMicrosoftLoginUrl();
        }

        var package = new DataPackage();
        package.SetText(loginUrl);
        Clipboard.SetContent(package);
        MicrosoftOutputBox.Text = LoginHelpText(loginUrl, "登录链接已复制到剪贴板。请在浏览器中打开并完成授权。");
        OutputBox.Text = "登录链接已复制。";
    }

    private void OpenMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        var loginUrl = CurrentMicrosoftLoginUrl();
        if (string.IsNullOrWhiteSpace(loginUrl))
        {
            GenerateMicrosoftLogin(sender, e);
            loginUrl = CurrentMicrosoftLoginUrl();
        }

        Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });
        MicrosoftOutputBox.Text = LoginHelpText(loginUrl, "已打开浏览器。完成授权后点击“等待回调并登录”。");
        OutputBox.Text = "已打开浏览器。";
    }

    private async void WaitMicrosoftCallback(object sender, RoutedEventArgs e)
    {
        await WaitMicrosoftCallbackAsync();
    }

    private async Task WaitMicrosoftCallbackAsync()
    {
        try
        {
            MicrosoftOutputBox.Text = "正在等待浏览器回调...";
            var callback = await _oauthCallbackServer.WaitForCodeAsync(AppConstants.RedirectUri, TimeSpan.FromMinutes(5));
            _microsoftAuthenticator.AuthorizationCode = callback.Code;
            await _microsoftAuthenticator.AuthenticateAsync();
            var profile = await _microsoftAuthenticator.GetMinecraftProfileAsync();
            var account = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["type"] = "microsoft",
                ["display_name"] = profile.Name,
                ["username"] = profile.Name,
                ["uuid"] = profile.Id,
                ["access_token"] = _microsoftAuthenticator.MinecraftAccessToken ?? "",
                ["refresh_token"] = _microsoftAuthenticator.RefreshToken ?? "",
            };
            UpsertAccount(account, findDuplicate: true);
            Refresh(account["id"]?.GetValue<string>());
            MicrosoftOutputBox.Text = $"Microsoft 账号已添加：{profile.Name}";
            OutputBox.Text = "Microsoft 登录完成。";
        }
        catch (Exception exc)
        {
            MicrosoftOutputBox.Text = exc.Message;
            OutputBox.Text = "Microsoft 登录失败。";
        }
    }

    private async void LoginExternalAccount(object sender, RoutedEventArgs e)
    {
        try
        {
            OutputBox.Text = "正在登录外置账号...";
            var injectorPath = AuthlibPathBox.Text.Trim();
            if (injectorPath.Length == 0 || !File.Exists(injectorPath))
            {
                ExternalStatusText.Text = "Authlib：未选择或不存在";
                OutputBox.Text = "请选择有效的 authlib-injector.jar。";
                return;
            }

            ExternalStatusText.Text = "正在登录外置账号...";
            var service = new ExternalAuthService();
            var account = await service.AuthenticateExternalAccountAsync(ExternalServerBox.Text, ExternalUsernameBox.Text, ExternalPasswordBox.Password);
            account["id"] = Guid.NewGuid().ToString();
            account["type"] = "external";
            account["auth_server"] = ExternalAuthService.NormalizeAuthServer(ExternalServerBox.Text);
            account["authlib_injector_path"] = injectorPath;
            UpsertAccount(account, findDuplicate: true);
            Refresh(account["id"]?.GetValue<string>());
            ExternalStatusText.Text = $"外置登录成功：{AccountLabel(account)}";
            OutputBox.Text = "外置账号已添加。";
        }
        catch (Exception exc)
        {
            ExternalStatusText.Text = "外置登录失败：" + exc.Message;
            OutputBox.Text = "外置登录失败：" + exc.Message;
        }
    }

    private void ExternalFormChanged(object sender, TextChangedEventArgs e) => UpdateExternalStatus();

    private void UpdateExternalStatus()
    {
        if (ExternalStatusText is null)
        {
            return;
        }

        var serverText = ExternalServerBox.Text.Trim();
        var parts = new List<string>();
        if (serverText.Length == 0)
        {
            parts.Add("服务器：未填写");
        }
        else
        {
            try
            {
                parts.Add("服务器：" + ExternalAuthService.NormalizeAuthServer(serverText));
            }
            catch (Exception exc)
            {
                ExternalStatusText.Text = "服务器地址无效：" + exc.Message;
                return;
            }
        }

        var injectorPath = AuthlibPathBox.Text.Trim();
        parts.Add(injectorPath.Length > 0 && File.Exists(injectorPath) ? "Authlib：已选择" : "Authlib：未选择或不存在");
        var username = ExternalUsernameBox.Text.Trim();
        if (username.Length > 0)
        {
            parts.Add("用户：" + username);
        }

        ExternalStatusText.Text = string.Join(" | ", parts);
    }

    private void PreviewAuthlibArgs(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = ExternalAuthService.AuthlibInjectorArgs(new JsonObject
            {
                ["type"] = "external",
                ["auth_server"] = ExternalServerBox.Text.Trim(),
                ["authlib_injector_path"] = AuthlibPathBox.Text.Trim(),
            });
            OutputBox.Text = string.Join(" ", args);
        }
        catch (Exception exc)
        {
            OutputBox.Text = exc.Message;
        }
    }

    private JsonObject? SelectedAccount()
        => CurrentAccountBox.SelectedItem is string label && _accountsByLabel.TryGetValue(label, out var account) ? account : null;

    private JsonObject? SelectedDeleteAccount()
        => DeleteAccountBox.SelectedItem is string label && _accountsByLabel.TryGetValue(label, out var account) ? account : null;

    private void UpsertAccount(JsonObject account, bool findDuplicate = false)
    {
        var id = account["id"]?.GetValue<string>() ?? "";
        var index = id.Length > 0 ? _accounts.FindIndex(item => item["id"]?.GetValue<string>() == id) : -1;
        if (index < 0 && findDuplicate)
        {
            var type = account["type"]?.GetValue<string>() ?? "";
            var username = account["username"]?.GetValue<string>() ?? "";
            var uuid = account["uuid"]?.GetValue<string>() ?? "";
            index = _accounts.FindIndex(item =>
                item["type"]?.GetValue<string>() == type &&
                ((uuid.Length > 0 && item["uuid"]?.GetValue<string>() == uuid) ||
                 (username.Length > 0 && item["username"]?.GetValue<string>() == username)));
            if (index >= 0)
            {
                account["id"] = _accounts[index]["id"]?.GetValue<string>() ?? id;
            }
        }

        if (index >= 0)
        {
            _accounts[index] = account;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                account["id"] = Guid.NewGuid().ToString();
            }

            _accounts.Add(account);
        }

        _settings.SaveAccounts(_accounts);
    }

    private void SaveMicrosoftLoginSettings()
    {
        _settings.LoadConfig();
        _settings.Config.Set("AUTH", "auto_open_browser", (AutoOpenBrowserCheckBox.IsChecked == true).ToString());
        _settings.SaveConfig();
    }

    private void ApplyAdvancedVisibility()
    {
        var advanced = _settings.Config.GetBool("UI", "advanced_mode", false);
        OfflineUuidBox.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        OfflineAccessTokenBox.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(OfflineNameBox, advanced ? 1 : 2);
    }

    private string CurrentMicrosoftLoginUrl()
    {
        var loginUrl = MicrosoftLoginLinkBox.Text.Trim();
        if (loginUrl.Length > 0)
        {
            _lastMicrosoftLoginUrl = loginUrl;
        }

        return _lastMicrosoftLoginUrl;
    }

    private static string LoginHelpText(string loginUrl, string hint)
        => string.Join(Environment.NewLine, [
            hint,
            "回调地址：http://localhost:5000/login/callback",
            "如果浏览器无法跳回启动器，请确认本机端口 5000 未被占用，然后重新生成链接。",
            "",
            loginUrl,
        ]);

    private static string AccountLabel(JsonObject account)
    {
        var type = account["type"]?.GetValue<string>() ?? "offline";
        var name = account["display_name"]?.GetValue<string>() ?? account["username"]?.GetValue<string>() ?? "(未命名)";
        return $"{name} ({type})";
    }

    private static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private string SelectedLoginMode()
        => LoginModeBox.SelectedItem?.Tag?.ToString() ?? "overview";

    internal void RunSmokeChecks()
    {
        var advancedVisibility = _settings.Config.GetBool("UI", "advanced_mode", false) ? Visibility.Visible : Visibility.Collapsed;
        if (OfflineUuidBox.Visibility != advancedVisibility || OfflineAccessTokenBox.Visibility != advancedVisibility)
        {
            throw new InvalidOperationException("账号高级字段可见性与高级模式不一致。");
        }

        foreach (var item in LoginModeBox.Items.OfType<SelectorBarItem>())
        {
            LoginModeBox.SelectedItem = item;
            var mode = item.Tag?.ToString() ?? "";
            var expectedEditorVisibility = mode == "overview" ? Visibility.Collapsed : Visibility.Visible;
            if (CurrentAccountCard.Visibility != (mode == "overview" ? Visibility.Visible : Visibility.Collapsed)
                || AccountEditors.Visibility != expectedEditorVisibility
                || OfflineAccountCard.Visibility != (mode == "offline" ? Visibility.Visible : Visibility.Collapsed)
                || MicrosoftAccountCard.Visibility != (mode == "microsoft" ? Visibility.Visible : Visibility.Collapsed)
                || ExternalAccountCard.Visibility != (mode == "external" ? Visibility.Visible : Visibility.Collapsed))
            {
                throw new InvalidOperationException($"账号分段 {mode} 的可见性不正确。");
            }
        }

        LoginModeBox.SelectedItem = LoginModeBox.Items[0];
    }

    internal void SelectLoginModeForSmoke(string mode)
    {
        SelectLoginMode(mode);
        ResponsiveLayoutRoot.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

}
