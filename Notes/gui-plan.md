# PdfWatcher GUI Plan

## Library

**WPF** alongside the existing WinForms tray icon.

- WPF allows full XAML-based custom styling — the only way to achieve the Adobe dark-panel look precisely
- WinForms stays for `NotifyIcon` (WPF has no built-in tray component)
- Both run on the same STA thread; WinForms `Application.Run()` pumps messages for both
- No third-party UI framework — pure custom XAML keeps the binary lean and styling fully controlled
- Added to the project with `<UseWPF>true</UseWPF>` alongside `<UseWindowsForms>true</UseWindowsForms>`

---

## Design Language

Modelled on Adobe Acrobat / Creative Cloud desktop apps.

| Element              | Value       |
|----------------------|-------------|
| Window background    | `#1E1E1E`   |
| Panel / card bg      | `#2C2C2C`   |
| Sidebar              | `#252525`   |
| Accent (Adobe red)   | `#EB1000`   |
| Primary text         | `#F0F0F0`   |
| Secondary text       | `#9A9A9A`   |
| Success              | `#26B566`   |
| Warning              | `#F5A623`   |
| Error                | `#F02B2B`   |
| Border               | `#3A3A3A`   |
| Font                 | Segoe UI    |
| Icons                | Segoe MDL2 Assets (built into Windows 10/11) |

---

## File Structure

```
PdfWatcher/
├── Program.cs                    ← tray + workers + AppState wiring
├── AppConfig.cs
├── AppState.cs                   ← shared observable state (queue, activity, logs)
├── Gui/
│   ├── AppStateSink.cs           ← custom Serilog sink → AppState.LogEntries
│   ├── MainWindow.xaml           ← shell: custom title bar + sidebar + content area
│   ├── MainWindow.xaml.cs
│   ├── ViewModels/
│   │   ├── ViewModelBase.cs      ← INotifyPropertyChanged + RelayCommand
│   │   ├── MainViewModel.cs      ← navigation state (CurrentView, nav items)
│   │   ├── DashboardViewModel.cs ← live status, queue, activity feed
│   │   ├── SettingsViewModel.cs  ← config editing + save
│   │   └── LogViewModel.cs       ← filterable log viewer
│   ├── Views/
│   │   ├── DashboardView.xaml
│   │   ├── DashboardView.xaml.cs
│   │   ├── SettingsView.xaml
│   │   ├── SettingsView.xaml.cs
│   │   ├── LogView.xaml
│   │   └── LogView.xaml.cs
│   └── Resources/
│       ├── Colors.xaml           ← palette defined once, referenced everywhere
│       └── Styles.xaml           ← buttons, inputs, sidebar nav items, scrollbar
```

---

## Window Layout

Custom title bar (no system chrome) with draggable top bar, minimize and close buttons.
Sidebar on the left (~52px) with icon-only nav items. Content area fills the rest.

```
┌─────────────────────────────────────────────────────┐
│  ▌ PdfWatcher                            [─]  [✕]  │  ← custom title bar (#252525)
├──────┬──────────────────────────────────────────────┤
│  ◉   │                                              │
│      │   [active view renders here]                 │
│  ⚙   │                                              │
│      │                                              │
│  ≡   │                                              │
│      │                                              │
└──────┴──────────────────────────────────────────────┘
```

---

## Navigation

Sidebar `ListBox` with icon-only items. `SelectedItem` drives `MainViewModel.CurrentView`.
`ContentControl` + `DataTemplate` map ViewModels to Views automatically.

| Icon (Segoe MDL2) | View      | Purpose                                  |
|--------------------|-----------|------------------------------------------|
| `&#xE80F;`         | Dashboard | Status, queue, quota counter, activity   |
| `&#xE713;`         | Settings  | Edit and save all config values          |
| `&#xE9D5;`         | Logs      | Scrollable, filterable real-time log     |

---

## Views

### Dashboard

| Element           | Detail                                                                 |
|-------------------|------------------------------------------------------------------------|
| Status pill       | `● Running` / `● Processing` / `⚠ Quota Exhausted` — color-coded      |
| Queue counter     | Files currently waiting or in OCR                                      |
| Processed today   | Resets at midnight                                                     |
| Processed / month | Helps track the 200-file Adobe monthly quota                           |
| Recent activity   | Last 50 processed files — filename, timestamp, ✓ / ✗                  |
| Re-scan button    | Calls the same logic as the tray Re-scan item                          |

### Settings

| Element              | Detail                                              |
|----------------------|-----------------------------------------------------|
| Watch Folders        | List with Add (folder browser) and Remove buttons   |
| Backup Folder        | Single path with Browse button                      |
| Adobe credentials    | ClientId + masked ClientSecret with show/hide       |
| Max Concurrent Jobs  | Numeric spinner (1–8)                               |
| Max Search Depth     | Numeric spinner (1–10)                              |
| Backup Retention     | Numeric spinner + "days (0 = keep forever)" label   |
| Save button          | Writes config.json — triggers hot-reload            |
| Unsaved indicator    | Subtle label when local edits differ from saved     |

### Logs

| Element          | Detail                                              |
|------------------|-----------------------------------------------------|
| Level filter     | Toggle buttons: INF / WRN / ERR / FTL               |
| Live tail        | Auto-scrolls as new entries arrive                  |
| Pause on scroll  | Stops auto-scroll when user scrolls up manually     |
| Clear button     | Clears the in-memory list (file is unaffected)      |
| Open log folder  | Opens the `logs/` directory in Explorer             |

---

## AppState

Central observable object created in `Program.cs` and shared with all ViewModels.
Workers update it directly (thread-safe via `Interlocked` + dispatcher marshaling).

```
AppState
├── Status: AppStatus          (Running / Processing / QuotaExhausted)
├── QueueDepth: int
├── ProcessedToday: int
├── ProcessedThisMonth: int
├── RecentActivity: ObservableCollection<ActivityEntry>
└── LogEntries: ObservableCollection<LogEntry>  ← fed by AppStateSink
```

---

## Tray ↔ Window Integration

- **Double-click tray icon** → show / focus window
- **Window close button (✕)** → hides window, app keeps running in tray
- **Tray Exit** → shuts down WPF app + cancels main loop
- Re-scan and quota alerts work from both tray and dashboard

---

## Future-Proofing

| Potential feature                | How the plan accommodates it                                    |
|----------------------------------|-----------------------------------------------------------------|
| Per-folder OCR settings          | SettingsView list is folder-based; ViewModel can be extended    |
| Quota usage chart                | DashboardViewModel already tracks monthly count                 |
| Drag-and-drop files onto window  | DashboardView drop zone → push path into channel               |
| Notification preferences         | New section in SettingsView, no structural change               |
| Multiple config profiles         | `AppConfig` is already an isolated class                        |
| Dark/light theme toggle          | All colors in `Colors.xaml` — swap one resource dictionary      |
