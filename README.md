# MyAppsFolder

A tiny Windows (WPF, .NET 8) launcher that shows a folder of shortcuts as a
floating, rounded icon grid over your wallpaper — like tapping a folder on an
iOS/macOS home screen — instead of opening File Explorer.

![screenshot](docs/screenshot.png) <!-- add your own -->

## What it does

- Reads shortcuts from `%APPDATA%\MyApps` and shows them as a clickable icon grid.
- **Subfolders become tabs.** Put shortcuts in `%APPDATA%\MyApps\Games`,
  `%APPDATA%\MyApps\Work`, etc. and each shows as a pill at the top.
- **Tab order** follows a leading number on the folder name: `1 Games`, `2 Work`,
  `10 Media`. The number (and a following `.`, `)`, `-`, or space) is stripped
  from the tab label. Unnumbered folders sort after, alphabetically.
- The grid **auto-fits**: it picks a column count and icon size so the whole tab
  fits on screen with no scrollbar. Very full folders get smaller icons.
- Real 256px icons for `.exe`, `.lnk`, and `.url` (incl. Steam per-game icons),
  with a coloured first-letter tile as a fallback. Failures are logged in plain
  English to `icon_debug.log` next to the exe.
- Clear panel over the live wallpaper (no frost), pixel-snapped for sharp text.
- Dismiss with **Esc**, a click outside the panel, the **✕**, or by switching
  focus to another window. The **📁** button opens the current tab's folder in
  Explorer so you can drag shortcuts in.
- **Drag a shortcut onto the .exe** (or a shortcut to it) and it's added to the
  folder silently instead of opening the popup.

## Build

Needs the **.NET 8 SDK** (`dotnet --version` should print `8.x`).

```sh
dotnet build MyAppsFolder/MyAppsFolder.csproj -c Release
```

### Standalone .exe (no .NET install required on the target machine)

```sh
dotnet publish MyAppsFolder/MyAppsFolder.csproj -c Release -r win-x64 --self-contained true
```

Output: `MyAppsFolder/bin/Release/net8.0-windows/win-x64/publish/`. Keep the whole
`publish` folder together; make a shortcut to `MyAppsFolder.exe` and put that on
your desktop.

## Configure

Edit the two lines at the top of [`App.xaml.cs`](MyAppsFolder/App.xaml.cs):

```csharp
public static readonly string FolderPath =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyApps");
public const string FolderTitle = "My Apps";
```

Point `FolderPath` anywhere you like (e.g. a fixed path, or
`SpecialFolder.DesktopDirectory`).

## Project layout

| File | Purpose |
|------|---------|
| `App.xaml` / `App.xaml.cs` | Startup, folder path, "dragged onto the exe" handling, empty-folder check, legacy-folder self-heal |
| `MainWindow.xaml` / `.xaml.cs` | The popup: tab bar, auto-fitting icon grid, animations, dismissal, launch |
| `IconHelper.cs` | Resolves 256px icons for folders / `.lnk` / `.url` / `.exe`; transparent-border trim; fallback |
| `AcrylicHelper.cs` | Optional Windows blur-behind (not currently wired up — see the note in the file) |

## Optional: app icon

Drop an `AppIcon.ico` next to `MyAppsFolder.csproj` and uncomment the
`<ApplicationIcon>` line in the `.csproj` to give the built `.exe` a custom icon.
