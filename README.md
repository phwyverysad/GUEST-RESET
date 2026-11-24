# GUEST-RESET

GUEST-RESET is a specialized, high-performance desktop application designed for Free Fire guest account management, automated token deployment, and full memory-based guest state resetting on Android emulators (BlueStacks 5 and MSI App Player).

The tool runs as a completely standalone, self-contained single-file executable requiring no external runtime installations.

---

## Features

- Single-File Portable Executable: Built as a fully self-contained win-x64 binary with all UI assets, native libraries, and .NET runtimes embedded.
- In-Process Native ADB Client: Implements direct raw TCP socket communication with the ADB server (127.0.0.1:5037). Zero external processes (such as HD-Adb.exe or conhost.exe) are spawned during operation.
- In-Memory AoB Patcher: Performs high-speed virtual memory chunk scanning and Array-of-Bytes (AoB) patching directly against the HD-Player emulator process.
- Non-Destructive Reset: Wipes Garena authentication tokens, guest files, and system account records while preserving 100 percent of game asset downloads and contentcache.
- Clean Minimalist User Interface: Powered by Microsoft Edge WebView2 with dual theme support (Light and Dark modes) and complete bilingual localization (English and Thai).
- Real-Time UID Parsing: Automatically parses incoming guest JSON payloads, displaying a clean preview bar with the guest UID and code validity state.
- Security and Sandbox Hardening: Enforces context menu handling, blocks developer tool shortcuts (F12, Ctrl+Shift+I), and prevents unauthorized external window creation.

---

## System Requirements

- Operating System: Windows 10 (version 1803 or higher) or Windows 11 (64-bit)
- Emulator: BlueStacks 5 or MSI App Player with Android Debug Bridge (ADB) enabled
- WebView2: Microsoft Edge WebView2 Runtime (standard on modern Windows installations)

---

## Architecture Overview

### 1. User Interface Layer
- HTML5, Tailwind CSS, and Vanilla JavaScript.
- Embedded as an assembly resource (`ui.html`) and extracted at startup to `%TEMP%\GuestResetApp_Bundle` if not running alongside loose asset files.
- Communicates bidirectionally with the host C# application through asynchronous WebView2 WebMessage APIs.

### 2. Core Application Layer (C# .NET 8)
- WinForms borderless window styled with Win32 GDI `CreateRoundRectRgn` and `CS_DROPSHADOW`.
- Theme synchronization mechanism dynamically updates both native form backbuffer colors and WebView2 background rendering to prevent visual artifacts along window borders.

### 3. Native ADB TCP Protocol Implementation
- Manages low-level protocol packets directly over `127.0.0.1:5037`:
  - `host:devices`: Queries connected and authorized devices.
  - `host:connect:<address>`: Establishes emulator network ADB connections.
  - `host:transport:<device>`: Routes commands to the targeted emulator instance.
  - `shell:<command>`: Executes non-blocking shell routines.
  - `WriteFileViaBase64`: Safely writes binary payloads to Android `/sdcard/` storage without corrupting character encodings.

### 4. Memory Patcher (AoB)
- Employs Win32 memory APIs (`OpenProcess`, `VirtualQueryEx`, `ReadProcessMemory`, `VirtualProtectEx`, `WriteProcessMemory`).
- Scans memory pages in 16 MB chunks with overlapping boundary buffers to identify target byte signatures and replace them with hook vectors in real time.

---

## Building from Source

### Prerequisites
- .NET 8.0 SDK (x64)

### Compile Single-File Executable
To generate the standalone executable:

```powershell
dotnet publish App/GuestResetApp.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o Bin/SingleFile
```

The resulting `GuestResetApp.exe` can be copied and executed on any compatible 64-bit Windows machine.

---

## Project Structure

```
GUEST-RESET/
|-- App/
|   |-- GuestResetApp.csproj       Project configuration and package references
|   |-- Program.cs                 Application entry point
|   |-- MainForm.cs                WinForms host, NativeAdb, and memory patching logic
|   |-- ui.html                    WebView2 frontend interface
|   |-- app.ico                    Application icon
|   |-- app_icon.png               Raster logo asset
|-- .gitignore                     Git ignore rules for build and temporary files
|-- README.md                      Project documentation
```

---

## License

This project is released for private development and research purposes. All trademarks, game titles, and logos belong to their respective owners.
