FluxLab — macOS

To install:
  Drag FluxLab.app onto the Applications folder in this window.

First launch:
  macOS Gatekeeper may warn that FluxLab "cannot be verified" because the app is
  ad-hoc signed rather than notarized. To open it the first time:
    - Right-click (or Control-click) FluxLab.app -> Open -> Open, OR
    - System Settings -> Privacy & Security -> "Open Anyway".
  You only need to do this once.

FluxLab is an astronomy image viewer with aperture photometry and exposure
metering for FITS images. Your saved camera profiles live in
~/Library/Application Support (kept across upgrades).

Note: the in-app Plate Solve feature drives a separately-installed StarFix.
FluxLab looks for StarFix in the standard macOS install location; if it isn't
auto-detected, point FluxLab at it via Tools -> Plate Solver. Everything else
(viewing, photometry, WCS readout, exposure meter) works normally.

Source & issues: https://github.com/ArtTrail/FluxLab
Released under the MIT License (see LICENSE in this disk image).
