# xClipboard

A clipboard history manager for Windows — my replacement for Win+V. C# / WPF, no external
dependencies, runs from the tray and opens as a fast popup.

## Features

- **Full history** — text, images and files, with search across everything.
- **Tabs** — separate streams for work and personal clipboard content, each with its own
  retention rules.
- **Refund sheet** — a Work tab feature that recognises pasted refund records and
  automatically builds and sorts a spreadsheet-style sheet.
- **Pinning** — keep important entries regardless of retention.
- **Themes** — several complete themes with custom fonts and accents.
- **Keyboard-first** — open, search, navigate and paste without touching the mouse.
- **Low overhead** — a single small process, no database server; storage is local.

## Tech

C# · .NET (WPF) · Win32 clipboard APIs · custom native interop (`Native.cs`)

## Architecture

```
app/
├── App.xaml / MainWindow.xaml   shell and popup UI
├── ClipboardService.cs          clipboard monitoring and history store
├── RefundParser.cs              refund record detection and sheet building
├── RefundWindow.xaml            refund sheet UI
├── Models.cs, ViewModels.cs     data model and MVVM layer
├── Native.cs                    Win32 interop
└── Theme.cs                     theming
```

## Build

Requirements: .NET SDK (Windows).

```powershell
.\build.ps1
```

or

```powershell
dotnet build app\xClipboard.csproj -c Release
```

## License

MIT — see [LICENSE](LICENSE).
