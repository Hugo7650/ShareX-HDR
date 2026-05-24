# ShareX Copilot Instructions

## Project Overview
ShareX is a mature Windows desktop application (.NET 9.0, WinForms) for screen capture, file sharing, and productivity. It's distributed via multiple channels (Setup, Portable, Steam, Microsoft Store) with 18+ years of development history.

## Architecture

### Multi-Project Solution Structure
The solution contains specialized libraries with clear boundaries:
- **ShareX** (main): WinForms UI, task orchestration, managers, and application lifecycle
- **ShareX.HelpersLib**: Core utilities (file operations, networking, image processing, system integration)
- **ShareX.UploadersLib**: 100+ uploader implementations (image hosts, file hosts, URL shorteners, text uploaders)
- **ShareX.ScreenCaptureLib**: Screen capture engine, annotation tools, region selection
- **ShareX.ImageEffectsLib**: Image effect/filter pipeline
- **ShareX.MediaLib**: FFmpeg video/audio recording integration
- **ShareX.HistoryLib**: Upload history tracking and management
- **ShareX.IndexerLib**: Directory indexing for file cataloging
- **ShareX.NativeMessagingHost**: Browser extension communication
- **ShareX.Setup**: Inno Setup-based installer generation
- **ShareX.Steam**: Steam-specific integrations

### Task Processing Pipeline
The core workflow revolves around the **task system** - all user actions (captures, uploads, file operations) are modeled as tasks:

1. **Entry Points**: [TaskHelpers.cs](ShareX/TaskHelpers.cs) - Main static methods for initiating work (e.g., `ExecuteJob`, screen capture methods, file operations)
2. **Task Creation**: [WorkerTask.cs](ShareX/WorkerTask.cs) - Factory methods create strongly-typed tasks (`CreateFileUploaderTask`, `CreateImageUploaderTask`, etc.)
3. **Task Management**: [TaskManager.cs](ShareX/TaskManager.cs) - Central coordinator that queues, executes, and tracks all tasks with event-driven progress updates
4. **Task Configuration**: [TaskSettings.cs](ShareX/TaskSettings.cs) - Comprehensive settings object (capture options, upload destinations, after-capture actions, file naming patterns)
5. **Execution**: [UploadManager.cs](ShareX/UploadManager.cs) - Orchestrates file uploads, URL shortening, and sharing operations

**Key Classes:**
- `TaskInfo`: Metadata container (file path, status, upload destination, progress, results)
- `WorkerTask`: Task executor with lifecycle events (`StatusChanged`, `UploadStarted`, `UploadCompleted`, `TaskCompleted`)
- `UploadResult`: Standardized upload response (URL, thumbnail URL, deletion URL, errors)

### Platform-Specific Builds
Multiple build configurations with conditional compilation:
- **Release/Debug**: Standard builds with debugging support
- **Steam**: `#if STEAM` - Steam achievement/overlay integration
- **MicrosoftStore**: `#if MicrosoftStore` - UWP sandbox constraints, no auto-updates
- **MicrosoftStoreDebug**: Debug build for Store testing

Build configurations are defined in [Directory.build.props](Directory.build.props) with custom `DefineConstants`.

## Development Conventions

### Code Style
- **Indentation**: 4 spaces (see [.editorconfig](.editorconfig))
- **Line endings**: CRLF (Windows-native)
- **Naming**: PascalCase for public members, camelCase for private fields
- **License headers**: All `.cs` files start with GPL v3 header block

### Manager Pattern
Static manager classes are the primary organizational pattern:
- `TaskManager`: Task lifecycle and UI updates
- `UploadManager`: Upload/download/sharing operations
- `SettingManager`: Settings persistence and loading
- `HotkeyManager`: Global hotkey registration
- `WatchFolderManager`: File system monitoring
- `ScreenRecordManager`: Screen recording sessions
- `CleanupManager`: Temporary file cleanup

Managers typically expose static methods and maintain internal state.

### Localization
Extensive i18n support with 20+ languages:
- Resource files: `*.resx` for default (English), `*.{lang}.resx` for translations (e.g., `AboutForm.zh-CN.resx`)
- Access via `Resources.ResourceName` or `GetLocalizedDescription()` extension methods
- Enums decorated with `// Localized` comments require translation support

### Event-Driven Updates
Task progress flows through events rather than polling:
```csharp
task.StatusChanged += Task_StatusChanged;
task.UploadProgressChanged += Task_UploadProgressChanged;
task.UploadCompleted += Task_UploadCompleted;
```
TaskManager subscribes to these events to update ListView/ThumbnailView UI components.

## Build & Test

### Building
```powershell
# Restore and build (specific configuration)
dotnet restore --runtime win-x64 ShareX.sln
dotnet build --configuration Release --self-contained true ShareX.sln

# Build all configurations (includes Steam, MicrosoftStore)
dotnet build --configuration Debug ShareX.sln
dotnet build --configuration Steam ShareX.sln
```

### CI/CD
GitHub Actions workflow ([.github/workflows/build.yml](.github/workflows/build.yml)):
- Builds all configurations (Release, Debug, Steam, MicrosoftStore, MicrosoftStoreDebug)
- Runs `ShareX.Setup.exe` to generate installer artifacts
- Artifacts: `ShareX-{version}-setup.exe`, `ShareX-{version}-portable.zip`

### Running
Main project: [ShareX/ShareX.csproj](ShareX/ShareX.csproj)
- Target: `.NET 9.0-windows10.0.22621.0` (Windows 11 SDK)
- Platform: `win-x64` only (x64 architecture required)
- Output: `ShareX/bin/{Configuration}/win-x64/ShareX.exe`

## Key Patterns

### TaskSettings Propagation
Every operation accepts `TaskSettings taskSettings = null`:
- If `null`, falls back to `TaskSettings.GetDefaultTaskSettings()`
- Allows per-task customization while maintaining global defaults
- TaskSettings contain 100+ options (destinations, file naming, after-capture actions, etc.)

### Uploader Architecture
All uploaders in `ShareX.UploadersLib` follow a service/factory pattern:
- **Services**: Metadata objects (e.g., `ImageUploaderService`, `FileUploaderService`) with configuration validation
- **Factory**: `UploaderFactory` creates uploader instances from service definitions
- **Base Classes**: `GenericUploader` → `ImageUploader`/`FileUploader`/`TextUploader`/`URLShortener`
- Example: [ShareX.UploadersLib/ImageUploaders/](ShareX.UploadersLib/ImageUploaders/), [ShareX.UploadersLib/FileUploaders/](ShareX.UploadersLib/FileUploaders/)

### After-Capture Task Pipeline
`AfterCaptureTasks` enum uses flags for composable post-capture actions:
```csharp
[Flags] AfterCaptureTasks {
    AnnotateImage, CopyImageToClipboard, SaveImageToFile, 
    UploadImageToHost, PerformActions, ScanQRCode, DoOCR, ...
}
```
See [TaskHelpers.cs](ShareX/TaskHelpers.cs) for pipeline execution logic.

### Resource Management
- **Images**: Use `using` statements for `Bitmap`/`Image` objects
- **Streams**: Properly dispose with `using` or explicit `.Dispose()`
- **Tasks**: `WorkerTask` implements `IDisposable` - TaskManager handles cleanup

## Common Pitfalls

1. **Don't bypass TaskManager**: Always use `TaskManager.Start(task)` for task execution, never call `task.Start()` directly
2. **Platform checks**: When adding platform-specific features, use `#if STEAM` or `#if MicrosoftStore` conditionals
3. **Localization**: New user-facing strings must be added to `Resources.resx` (auto-generates `Resources.Designer.cs`)
4. **TaskSettings nullability**: Always handle `taskSettings == null` by calling `TaskSettings.GetDefaultTaskSettings()`
5. **Thread safety**: UI updates from task events must use `InvokeAsync` or check `InvokeRequired`

## Adding Features

### New Upload Destination
1. Add service to `ShareX.UploadersLib/{ImageUploaders|FileUploaders|TextUploaders|URLShorteners}/`
2. Implement base class (`ImageUploader`, `FileUploader`, etc.)
3. Register in `UploaderFactory` service dictionaries
4. Add enum value to relevant destination enum (`ImageDestination`, `FileDestination`, etc.)
5. Create config UI form if OAuth/API keys needed

### New Capture Type
1. Add method to [TaskHelpers.cs](ShareX/TaskHelpers.cs) (follows `ExecuteJob` patterns)
2. Create `WorkerTask` via factory method in [WorkerTask.cs](ShareX/WorkerTask.cs)
3. Add hotkey enum value to `HotkeyType` in [Enums.cs](ShareX/Enums.cs)
4. Register hotkey in [HotkeyManager.cs](ShareX/HotkeyManager.cs)
5. Add UI menu item/button in [MainForm.cs](ShareX/Forms/MainForm.cs)

### New After-Capture Task
1. Add flag to `AfterCaptureTasks` enum in [Enums.cs](ShareX/Enums.cs)
2. Implement handler in [TaskHelpers.cs](ShareX/TaskHelpers.cs) `DoAfterCaptureTasks` method
3. Add localized description to `Resources.resx`
4. Add checkbox to [TaskSettingsForm.cs](ShareX/Forms/TaskSettingsForm.cs) capture settings tab

## Dependencies
Key NuGet packages:
- **Newtonsoft.Json** (13.0.3): JSON serialization for settings/config
- **ZXing.Net** (0.16.10): QR code scanning/generation
- **FluentFTP** (52.1.0): FTP/FTPS uploads
- **SSH.NET** (2025.0.0): SFTP uploads
- **MegaApiClient** (1.10.4): MEGA.nz uploads

External tools (in `Libs/`):
- **FFmpeg**: Video recording/encoding (not committed, downloaded at runtime)
- DirectX/Windows SDK: Screen capture APIs (OS-provided)
