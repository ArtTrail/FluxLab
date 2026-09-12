# Simple FITS Viewer

A Python/Tkinter desktop application for viewing and exploring FITS astronomy image files.

![Simple FITS Viewer v1.2.0](screenshots/simple_fits_viewer_main.png)

## Features

- **Open single files or entire directories** of FITS images
- **Frame navigation** — step through multi-frame FITS files or a directory of images using playback controls
- **Zoom controls** — Zoom In, Zoom Out, Fit to window, and 1:1 pixel view
- **Stretch modes** — Linear, ZScale, and other contrast stretches
- **Colormaps** — gray, heat, viridis, and more
- **Interactive histogram** — drag black/white level markers or use sliders; Auto Stretch button
- **WCS coordinate readout** — live X, Y, ADU, RA, and Dec display as you move the cursor
- **FITS Header viewer** — browse the full header in a resizable window
- **Aperture photometry** — click a star to auto-centroid and size an aperture/sky annulus, with Gain/ADU-scale/Full-well/Target-electron inputs and an Exposure Meter (bar gauges for signal-vs-target and peak-vs-saturation, plus a color-coded status and increase/reduce/no-change recommendation)
- **Toolbar menus** — File, View, Image, and Help (User Guide, Revision History, About)

## Requirements

- Windows 10/11
- Python 3.10+ with the following packages:
  - `astropy`
  - `astropy_iers_data`
  - `numpy`
  - `matplotlib`
  - `Pillow`

## Installation

```bash
pip install astropy astropy-iers-data numpy matplotlib Pillow
python fits_viewer.py
```

## Building the Executable

Requires [PyInstaller](https://pyinstaller.org/):

```bash
pip install pyinstaller
pyinstaller fits_viewer.spec
```

The onedir build will be created in the `dist/` folder.

> **Note:** The PyInstaller spec must collect data files for `astropy`, `astropy_iers_data`, `asdf`, and `asdf_astropy` — the ASDF JSON schemas are required at runtime.

## Usage

1. Click **Open** to load a single FITS file, or **Open Dir** to load all FITS files in a folder.
2. Use the **Stretch** and **Colormap** dropdowns to adjust the display.
3. Adjust contrast with the **histogram** sliders or click **Auto Stretch**.
4. Use the playback controls at the bottom to navigate between frames or files.
5. Hover over the image to see live pixel coordinates and WCS (RA/Dec) in the toolbar.
6. Click **Header** to inspect the full FITS header.

## Version History

| Version | Notes |
|---------|-------|
| 1.4.0   | Fixed Total-electrons overcount on calibrated/float FITS files (ADU-scale auto-detection is unreliable post-calibration); added Full well (e-) and Target electrons fields; replaced the Level readout with an Exposure Meter — bar gauges for signal-vs-target and peak-vs-saturation, plus a color-coded status and increase/reduce/no-change recommendation |
| 1.3.0   | Aperture photometry: click-to-centroid, sky annulus, Gain/ADU-scale, Total electrons, Level (Low/Nominal/High) readout, sequence playback support |
| 1.2.0   | Fixed 1 fps playback (threaded loader); trough-click slider override; header window stays open; toolbar RA/Dec readout; Help menu |
| 1.1.x   | Histogram controls; Auto Stretch; colormap selector |
| 1.0.0   | Initial release |

## License

MIT License — © Arthur T. Trail 2026. See [LICENSE](LICENSE) for details.
