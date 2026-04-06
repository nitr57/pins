# PI.N.S. Quick Start Guide

This guide walks you through building and running PI.N.S. on a Raspberry Pi (or any Linux ARM64/x64 machine) from scratch.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Clone the Repository](#2-clone-the-repository)
3. [Install the System.Windows Compatibility Stub](#3-install-the-systemwindows-compatibility-stub)
4. [Build the Application](#4-build-the-application)
5. [Run PI.N.S.](#5-run-pins)
6. [Connect to the Interface](#6-connect-to-the-interface)
7. [Command-Line Options](#7-command-line-options)
8. [Connecting Equipment](#8-connecting-equipment)
9. [Running at Boot (systemd)](#9-running-at-boot-systemd)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Prerequisites

### Hardware

- Raspberry Pi 4 or Pi 5 (4 GB RAM minimum recommended), or any Linux ARM64 / x64 machine
- USB ports available for astronomy equipment

### Software

Install the following on your Pi before proceeding:

```bash
# Update package lists
sudo apt-get update

# .NET 10 SDK (required to build)
# Download and install from https://dot.net or use the Microsoft package feed:
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0

# Add dotnet to PATH (add to ~/.bashrc or ~/.profile for persistence)
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"

# Required native libraries
sudo apt-get install -y \
    git \
    git-lfs \
    libgdiplus \
    libusb-1.0-0 \
    libc6 \
    libstdc++6

# INDI server and drivers (for INDI-based equipment)
sudo apt-get install -y indi-full
```

> **Note:** `libgdiplus` is required for image processing. `indi-full` installs the INDI server and common drivers; you can install individual driver packages (e.g. `indi-asi`, `indi-qhy`) instead if you want a smaller footprint.

---

## 2. Clone the Repository

```bash
git clone https://github.com/<your-org>/pins.git
cd pins

# Initialise submodules and sync LFS assets
git lfs install
git submodule update --init --recursive
git lfs pull
```

---

## 3. Install the System.Windows Compatibility Stub

PI.N.S. replaces the Windows-only `System.Windows.dll` with a Linux-compatible, OpenCV-backed implementation. This step installs that stub into your .NET runtime so it is found automatically at launch.

```bash
# Build the compat library and inject it into the .NET runtime
./install-system-windows-stub.sh
```

The script will:
1. Detect your installed .NET version automatically.
2. Build `System.Windows.Compat` in Release mode if not already built.
3. Back up the original `System.Windows.dll` in the framework directory.
4. Replace it with the PI.N.S. OpenCV-backed version.

To undo this at any time:
```bash
DOTNET_ROOT="${DOTNET_ROOT:-/usr/lib/dotnet}"
DOTNET_VERSION=$(ls "$DOTNET_ROOT/shared/Microsoft.NETCore.App" | sort -V | tail -n1)
sudo cp "$DOTNET_ROOT/shared/Microsoft.NETCore.App/$DOTNET_VERSION/System.Windows.dll.backup" \
        "$DOTNET_ROOT/shared/Microsoft.NETCore.App/$DOTNET_VERSION/System.Windows.dll"
```

---

## 4. Build the Application

### On the Raspberry Pi (native build — recommended)

Building directly on the Pi produces a self-contained binary optimised for your exact hardware. The project auto-detects `linux-arm64` when built on an ARM64 host.

```bash
dotnet publish NINA/NINA.csproj -c Release --self-contained true -o ./publish/pins
```

### Cross-compile from another machine (faster for development)

```bash
# From macOS or Linux x64 — target Raspberry Pi (ARM64)
dotnet publish NINA/NINA.csproj -c Release \
    --self-contained true \
    -r linux-arm64 \
    -o ./publish/pins

# Copy the output to the Pi
scp -r ./publish/pins pi@<pi-ip>:~/pins
```

> **Note:** The `NINA/External/` directory is populated from LFS. Ensure `git lfs pull` completed successfully before publishing; the native device SDK `.so` files (ASI, ToupTek, Nitecrawler, etc.) must be present in the output.

---

## 5. Run PI.N.S.

PI.N.S. runs as a **headless server**. There is no local GUI — all interaction happens through the web interface (see [Section 6](#6-connect-to-the-interface)).

```bash
cd ~/pins
./NINA
```

On first launch, PI.N.S. will:
- Create a default profile under `~/.local/share/nina/` (Linux XDG path).
- Start listening on `http://0.0.0.0:4782`.
- Connect to the local INDI server if one is running.

To suppress file-watcher warnings on Pi (low inotify limit), this is already handled in the application startup automatically.

### Useful flags

| Flag | Description |
|------|-------------|
| `--sequencefile <path>` / `-s` | Load a sequence file at startup |
| `--runsequence` / `-r` | Auto-start the loaded sequence |
| `--exitaftersequence` / `-x` | Exit after the sequence finishes (useful for automation) |
| `--profileid <id>` / `-p` | Start with a specific profile |
| `--debug` / `-d` | Enable debug mode |
| `--disable-hardware-acceleration` / `-g` | Disable UI hardware acceleration |

---

## 6. Connect to the Interface

PI.N.S. exposes a **SignalR / REST API** on port **4782**. You interact with it via a web frontend running on any browser on your local network.

Open a browser (on any machine on the same network) and navigate to:

```
http://<pi-ip-address>:4782
```

### Real-time hubs (SignalR)

| Hub path | Purpose |
|----------|---------|
| `/hubs/notifications` | Application-wide notifications |
| `/hubs/dialogs` | Dialog prompts |
| `/hubs/messageboxes` | Message box interactions |
| `/hubs/progress` | Sequence and operation progress |

---

## 7. Command-Line Options

```
./NINA [options]

  -p, --profileid               Load profile by given id at startup.
  -s, --sequencefile            Load a sequence file at startup.
  -r, --runsequence             Automatically start a sequence loaded with -s.
  -x, --exitaftersequence       Exit after the sequence has finished.
  -d, --debug                   Activate debug mode.
  -g, --disable-hardware-acceleration   Disable UI hardware acceleration.
```

---

## 8. Connecting Equipment

PI.N.S. supports two driver stacks:

### INDI (recommended on Linux)

Start your INDI server before launching PI.N.S.:

```bash
# Example: ZWO camera + EQMod mount
indiserver -v indi_asi_ccd indi_eqmod_telescope
```

PI.N.S. will automatically connect to the local INDI server. In the web interface, go to **Equipment** and select each device from the INDI driver list.

Tested INDI devices include:
- **Cameras:** ZWO ASI, ToupTek
- **Filter Wheels:** Astroasis, ZWO, ToupTek
- **Focusers:** Astroasis, Nitecrawler, QHY, ToupTek, ZWO, Gemini (indi_myfocuserpro2)
- **Rotators:** Nitecrawler, Wanderer
- **Mounts:** 10Micron, OnStep, ZWO
- **Flat Panels:** Gemini, Wanderer
- **Switches:** Svbony
- **Other:** ZWO Seestar (Alpaca)

### ASCOM Alpaca

For Alpaca-compatible devices, configure the Alpaca discovery in the Equipment settings panel and PI.N.S. will discover them on the local network automatically.

---

## 9. Running at Boot (systemd)

Create a systemd service so PI.N.S. starts automatically when the Pi boots:

```bash
sudo nano /etc/systemd/system/pins.service
```

Paste the following, adjusting paths as needed:

```ini
[Unit]
Description=PI.N.S. Astrophotography Server
After=network.target

[Service]
Type=simple
User=pi
WorkingDirectory=/home/pi/pins
ExecStart=/home/pi/pins/NINA
Restart=on-failure
RestartSec=5
Environment=DOTNET_ROOT=/home/pi/.dotnet
Environment=PATH=/home/pi/.dotnet:/home/pi/.dotnet/tools:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

[Install]
WantedBy=multi-user.target
```

Enable and start the service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable pins
sudo systemctl start pins

# Check status
sudo systemctl status pins

# View logs
journalctl -u pins -f
```

---

## 10. Troubleshooting

### `System.Windows.dll` not found or wrong version

Re-run the stub installer:
```bash
./install-system-windows-stub.sh
```

### Application fails to start: inotify limit exceeded

```bash
echo fs.inotify.max_user_watches=524288 | sudo tee -a /etc/sysctl.conf
sudo sysctl -p
```

### Native SDK `.so` files missing (ASI, ToupTek, etc.)

The device SDK libraries are stored in Git LFS under `NINA/External/`. Pull them explicitly:
```bash
git lfs pull
```
Then re-publish the application.

### Cannot connect to INDI server

Ensure `indiserver` is running before starting PI.N.S.:
```bash
ps aux | grep indiserver
# If not running:
indiserver -v indi_asi_ccd &
```

### Port 4782 not reachable from another machine

Check your Pi's firewall:
```bash
sudo ufw allow 4782/tcp
```

### Logs

Application logs are written to:
```
~/.local/share/nina/Logs/
```
