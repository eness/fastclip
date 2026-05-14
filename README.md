![Clipboard To Selected File](./banner.png)

# Clipboard To Selected File

🖼️ A lightweight Windows tray utility that takes the current image from your clipboard and writes it into the file currently selected in Windows Explorer.

## ✨ What It Does

- Replaces the selected image file with the clipboard image
- Runs quietly in the system tray
- Uses a global hotkey: `Ctrl+Shift+V`
- Works well for fast image replacement workflows

## 🧩 Supported Formats

- `.png`
- `.jpg`
- `.jpeg`
- `.bmp`
- `.gif`
- `.tif`
- `.tiff`

## ⚙️ How It Works

1. Copy any image to the clipboard.
2. Select a target image file in Windows Explorer.
3. Press `Ctrl+Shift+V`.

The app saves the clipboard image into the selected file using that file's extension and format.

## 📦 Build

This repository includes a GitHub Actions workflow that publishes a self-contained `win-x64` build.

The generated artifact includes a ready-to-run `ClipboardToSelectedFile.exe`, so the target machine does not need a separate `.NET` installation.

## 👤 Author

- Enes Sönmez
- X / Twitter: https://x.com/enes_dev
