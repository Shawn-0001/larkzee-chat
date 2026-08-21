# Theme and design tokens

## Part 1 — Compact token summary

### Platform

- Native .NET 8 WinForms, light-only; styling is imperative C#.
- Buttons, inputs, check boxes, group boxes, focus/disabled states use Windows system rendering. No dark theme, CSS/XAML, Tailwind, theme provider, icon library, or third-party UI controls.

### Colors

| Token | Value | Use |
|---|---:|---|
| Surface | `#FFFFFF` | Windows, header, composer, input |
| Conversation canvas | `#FAFAFA` | Message list |
| Body text | `#1E1E1E` | Messages/input |
| Title | `#232323` | Product title |
| Secondary | `#464646` | Key label |
| Muted | `#6E6E6E` | Disconnected state |
| Hint | `#737373` | Firewall advisory |
| Timestamp | `#7D7D7D` | Bubble time |
| Success | `#1E8246` | Connected state |
| Own bubble | `#E1F0FF` | Right bubble |
| Remote bubble | `#F0F0F0` | Left bubble |

### Typography

- Default/status/controls: Segoe UI 9 pt Regular.
- Title: Segoe UI Semibold 15 pt Bold.
- Message/composer: Segoe UI 10 pt Regular.
- Hint: Segoe UI 8.5 pt Italic; timestamp: Segoe UI 8 pt Regular.

### Spacing and sizing

- Main: 720x620 client, 560x450 minimum. Header height 66, padding (18,12,18,8); header actions minimum 86x30 with 10 gap.
- Feed padding (10,8,10,8). Composer height 112, padding (12,10,12,12); send width 84 with 10 gap.
- Message row vertical padding 5; edge margin 12; bubble padding (14,9,14,8); time gap 5; bubble cap 74% of row.
- Settings: 500x430 client, 460x390 minimum; root padding 18; groups use 12 horizontal / 22 top padding and 12/7 gaps; label column 94.
- Settings footer height 52, padding (18,5,18,12); save minimum 92x30.

### Shape, depth, responsiveness

- No app-defined radius or shadow; message panels are rectangular. Composer input is `BorderStyle.FixedSingle`.
- No breakpoints. Docking/anchors, minimum window sizes, and the continuous 74% bubble cap provide resizing behavior.

## Part 2 — Raw source inventory

No dedicated theme/token files exist. Full theme-bearing source is already preserved in `layouts.md` (`Forms/MainForm.cs`) and `components.md` (`Controls/ChatMessageControl.cs`); settings tokens are declared in `Forms/SettingsForm.cs`. The full project manifest below proves the WinForms framework and absence of UI package dependencies.

### LarkzeeChat.csproj (full source)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <AssemblyName>LarkzeeChat</AssemblyName>
    <RootNamespace>LarkzeeChat</RootNamespace>
    <Version>1.0.0</Version>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <InformationalVersion>1.0.0</InformationalVersion>

    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>

  <!-- Keep normal project references framework-dependent; the publish command
       supplies the explicit win-x64/self-contained target. -->
  <PropertyGroup Condition="'$(IsPublish)' == 'true'">
    <RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == ''">win-x64</RuntimeIdentifier>
    <SelfContained Condition="'$(SelfContained)' == ''">true</SelfContained>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
  </PropertyGroup>

  <ItemGroup>
    <Compile Remove="tests\**\*.cs" />
    <EmbeddedResource Remove="tests\**\*" />
    <None Remove="tests\**\*" />
  </ItemGroup>
</Project>
```
