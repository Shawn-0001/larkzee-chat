# Larkzee Chat

Larkzee Chat 是一个面向 Windows 局域网的轻量点对点聊天工具。两台电脑运行相同版本的程序，其中任意一台开启连接服务，另一台使用 IPv4 地址和手动设置的密码连接。程序不需要中央服务器、账号或数据库，也不保存聊天记录。

聊天消息只存在于当前进程内存和聊天窗口中，关闭程序后立即清空，不会写入配置文件或日志。为避免长时间连续运行导致界面控件无限增长，当前窗口最多保留最近 24 小时、500 条且累计 100,000 个字符以内的消息；任一上限触发后会从最旧消息开始清理。

当聊天窗口不在前台时，新收到的消息会让 Windows 任务栏按钮持续闪动；切回聊天窗口后自动停止，不会弹出打断操作的提示框。

## 首次使用

1. 两台电脑分别运行 `LarkzeeChat.exe`。
2. 接收连接的一方打开“⚙ 配置”，设置 8–64 个字符的“本机连接密码”，再开启“允许其他电脑连接”。建议使用至少 12 个字符且不与其他账号共用的密码。
3. Windows 首次询问防火墙权限时，仅允许“专用网络”。程序不会自动修改防火墙，也不需要管理员权限。
4. 将这台电脑的局域网 IPv4 地址和密码通过可信方式告诉另一方。
5. 另一方在“⚙ 配置”中填写“对方 IP”和“对方密码”，保存后回到主窗口点击“连接”。
6. 任意一方可点击“断开”。若网络中断或程序关闭，另一方会静默回到未连接状态，不弹出断线提示框，也不会清掉尚未发送的输入内容。

连接服务固定监听 IPv4 `0.0.0.0:45678`。IP 仅显示在配置窗口；主聊天窗口不会显示任何网络或身份技术信息。

IP、本机连接密码和对方密码保存在 `%USERPROFILE%\.larkzeeChat\settings.json`。两个密码使用 Windows DPAPI 的 `CurrentUser` 范围保护，配置文件中不写入密码明文；同一 Windows 用户再次启动程序时会自动读取。更换 Windows 用户或无法解密配置时，需要重新设置密码。手动修改本机密码只影响之后建立的新连接，不会中断当前已经加密的聊天会话。

## 构建与发布

需要 .NET 8 SDK：

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

默认发布文件位于：

```text
bin\Release\net8.0-windows\win-x64\publish\LarkzeeChat.exe
```

发布结果是 Windows x64、自包含、单文件应用，目标电脑不需要单独安装 .NET Runtime。

仓库中的可分发文件有两种，请不要混用：

- `dist\LarkzeeChat-v1.0.1-win-x64\LarkzeeChat.exe`：推荐拷贝到其他电脑使用；已经包含 .NET 8 Desktop Runtime，目标电脑无需另行安装运行环境。
- `dist\LarkzeeChat-v1.0.1-win-x64-lite\LarkzeeChat.exe`：小体积版本，只适合已经安装 .NET 8 Desktop Runtime x64 的电脑；裸机双击可能无法启动。
- 两个目录中的 `LarkzeeChat.exe.md5` 分别记录对应 EXE 的 MD5，复制后可用于完整性核对。

`dist/` 是本地发布输出目录，已从 Git 跟踪中排除。

## 本地冒烟测试

测试使用 localhost 模拟两台电脑，不依赖第三方测试框架：

```powershell
dotnet run -c Release --project .\tests\LarkzeeChat.SmokeTests\LarkzeeChat.SmokeTests.csproj
```

## v2 安全边界

连接认证使用手动密码、随机 Challenge、每次连接新生成的 NIST P-256 ECDH 密钥对和 HMAC-SHA256 双向证明；密码本身不会作为认证报文发送。认证失败会按来源 IP 限流。协议 v2 不兼容旧的明文版本，两台电脑必须同时更新。

认证成功后，聊天、心跳和断开报文都使用每个连接独立派生的 AES-256-GCM 会话密钥加密，并带有严格递增的方向序号，可检测篡改、重放和乱序。会话密钥只存在于当前连接内，断开或重连后不会复用。

安全边界：弱密码仍可能被抓包者离线猜测，因此不要只使用 8 位常见单词；DPAPI 主要防止配置文件被直接复制后读取，已在同一 Windows 用户身份下运行的恶意程序仍可能访问密码。网络上的地址、报文长度和时间等元数据也不会被隐藏。请仅在可信的本地局域网使用。
