# RouteFlow

基于 Avalonia 11 和 .NET 8 的 sing-box 桌面管理工具，同时支持 Windows x64 和 Ubuntu/Debian amd64。

## 功能

- 本机 TUN 分流：HTTP 上游、DNS over TLS、IP/CIDR 和域名路由
- 内网中转：Mixed HTTP/SOCKS 监听、认证和连接测试
- 配置修改后自动校验并保存，启动或重启时直接使用最新配置
- Windows UAC 和 Linux `pkexec` 权限提升
- 系统托盘：显示窗口、启动/停止、重启、切换模式和退出
- Windows 便携 ZIP 与 Ubuntu/Debian `.deb` 一键打包

## 目录

```text
.
|-- config.example.json       # 脱敏的默认配置模板
|-- packaging/                # 图标、Windows 和 Debian 打包脚本
|-- src/RouteFlow/       # Avalonia 应用源码
|-- third_party/sing-box/     # Windows/Linux amd64 核心及许可证
|-- RouteFlow.sln
`-- README.md
```

`.tools/`、`artifacts/`、`dist/`、`bin/`、`obj/` 和本机 `config.json` 均不提交 Git。

## 准备 sing-box

仓库已经包含 sing-box 1.13.15 的 Windows/Linux amd64 核心和 Cronet 库，不需要每次构建都重新下载。文件位于 `third_party/sing-box/`，官方许可证、版本和 SHA-256 校验值也保存在同一目录。

将 `config.example.json` 复制为 `config.json`，再填写实际上游和路由。若要升级核心，请同时替换两个平台的核心、更新 `VERSION` 和 `SHA256SUMS`。

不要把包含真实账号、密码、内网域名或地址的 `config.json` 提交到公开仓库。

## Windows 开发环境

要求：

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- PowerShell 5.1 或 PowerShell 7
- Visual Studio 2022、Rider 或 VS Code（可选）

```powershell
dotnet --info
Copy-Item .\config.example.json .\config.json
$env:SING_BOX_HOME = (Get-Location).Path
$env:SING_BOX_PATH = "$PWD\third_party\sing-box\win-x64\sing-box.exe"
dotnet restore .\RouteFlow.sln
dotnet run --project .\src\RouteFlow\RouteFlow.csproj
```

启动或停止 TUN 时程序会请求 UAC，不需要以管理员身份运行整个 UI。

## Ubuntu 开发环境

安装 [.NET 8 SDK](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu) 后，再安装 Avalonia、托盘和提权所需的系统库：

```bash
sudo apt update
sudo apt install -y \
  libx11-6 libice6 libsm6 libfontconfig1 libfreetype6 \
  libxext6 libxrender1 libxrandr2 libxi6 libxcb1 \
  libxkbcommon0 libxkbcommon-x11-0 libdbus-1-3 libgl1 \
  policykit-1 curl

dotnet --info
cp config.example.json config.json
export SING_BOX_HOME="$PWD"
chmod +x ./third_party/sing-box/linux-x64/sing-box
export SING_BOX_PATH="$PWD/third_party/sing-box/linux-x64/sing-box"
dotnet restore ./RouteFlow.sln
dotnet run --project ./src/RouteFlow/RouteFlow.csproj
```

不同 Ubuntu 版本的 `pkexec` 包名可能是 `pkexec` 或 `policykit-1`。GNOME 若禁用了系统托盘，需要启用 AppIndicator/StatusNotifier 扩展。

## 构建与检查

```powershell
dotnet restore .\RouteFlow.sln
dotnet build .\RouteFlow.sln -c Release
dotnet format .\RouteFlow.sln --verify-no-changes --no-restore
```

程序提供不启动 GUI 的配置自检：

```powershell
dotnet run --project .\src\RouteFlow\RouteFlow.csproj -- --self-test
```

## 打包 Windows ZIP 和 Debian DEB

推荐在 Windows 上执行统一打包脚本。脚本优先使用仓库内的 Windows/Linux amd64 核心，不需要联网；只有指定的 `CoreVersion` 与 `third_party/sing-box/VERSION` 不一致或核心文件缺失时才会从官方 GitHub Release 下载。脚本会发布自包含 .NET 运行时并生成两个安装包：

```powershell
.\packaging\Build-Release.ps1
```

使用本机配置作为安装包默认配置：

```powershell
.\packaging\Build-Release.ps1 -ConfigPath .\config.json
```

指定 SDK 或版本：

```powershell
.\packaging\Build-Release.ps1 `
  -DotNetPath "C:\Program Files\dotnet\dotnet.exe" `
  -AppVersion 1.0.0 `
  -CoreVersion 1.13.15
```

输出目录：

```text
artifacts/release/RouteFlow-1.0.0-win-x64-portable.zip
artifacts/release/routeflow_1.0.0-1_amd64.deb
```

Windows 解压 ZIP 后运行 `RouteFlow.exe`。Ubuntu/Debian 安装：

```bash
sudo apt install ./routeflow_1.0.0-1_amd64.deb
```

Linux 首次启动会把默认配置复制到 `${XDG_CONFIG_HOME:-$HOME/.config}/routeflow/config.json`。

## 命令行参数

- `--relay`：直接打开内网中转界面
- `--self-test`：校验客户端和中转端默认配置
- `--start-client` / `--start-relay`：特权助手内部入口
- `--restart-client` / `--restart-relay`：特权助手内部入口
- `--stop`：特权助手内部入口
