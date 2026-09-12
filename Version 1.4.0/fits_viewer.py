#!/usr/bin/env python3
"""
FITS Viewer - Astronomy image viewer
Features: autostretch, histogram with draggable level controls, video playback,
MP4 export, FITS header readout and editing.
"""

import json
import math
import os
import sys
import queue
import threading
import time
from pathlib import Path
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, simpledialog

# ---------------------------------------------------------------------------
# Dependency check
# ---------------------------------------------------------------------------
_missing = []
try:
    import numpy as np
except ImportError:
    _missing.append("numpy")

try:
    from astropy.io import fits
    from astropy.visualization import ZScaleInterval, PercentileInterval, MinMaxInterval
    from astropy.wcs import WCS
except ImportError:
    _missing.append("astropy")

try:
    from PIL import Image, ImageTk
except ImportError:
    _missing.append("Pillow")

try:
    import matplotlib
    matplotlib.use("TkAgg")
    from matplotlib.figure import Figure
    from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg
    import matplotlib.cm as _cm
except ImportError:
    _missing.append("matplotlib")

if _missing:
    _root = tk.Tk()
    _root.withdraw()
    messagebox.showerror(
        "Missing Dependencies",
        f"Please install:\n\npip install {' '.join(_missing)}"
    )
    sys.exit(1)

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
APP_VERSION = "1.4.0"

# Settings file — persists user preferences across sessions
_SETTINGS_PATH = (
    Path(sys.executable if getattr(sys, "frozen", False) else __file__).parent
    / "fits_viewer_settings.json"
)

COLORMAPS   = ["gray", "viridis", "plasma", "inferno", "hot", "cool", "Blues_r", "YlOrRd", "RdYlBu_r"]
STRETCH_MODES = ["ZScale", "Percentile 99.5%", "Percentile 98%", "MinMax", "Linear 5-99%"]

NAV_COLOR   = "#1b3f6b"
NAV_FG      = "#ffffff"
BG_COLOR    = "#1e1e2e"
FRAME_COLOR = "#252535"
TEXT_COLOR  = "#d8d8f0"
ACCENT      = "#4a9eff"
SEP_COLOR   = "#333355"


def _get_cmap(name):
    """Return a matplotlib colormap, compatible with older and newer matplotlib."""
    try:
        return matplotlib.colormaps[name]
    except (AttributeError, KeyError):
        return _cm.get_cmap(name)


# ---------------------------------------------------------------------------
# Main Application
# ---------------------------------------------------------------------------
class FITSViewer:
    def __init__(self, root: tk.Tk):
        self.root = root
        self._update_title()
        self.root.configure(bg=BG_COLOR)

        # --- Image state ---
        self.fits_file: str | None = None
        self.fits_data: np.ndarray | None = None   # raw float64
        self.fits_header = None                    # astropy Header
        self.display_image: Image.Image | None = None
        self.tk_image = None
        self._wcs = None                           # astropy WCS if plate-solved

        # --- Stretch state ---
        self.stretch_mode  = tk.StringVar(value="ZScale")
        self.black_level   = tk.DoubleVar(value=0.0)
        self.white_level   = tk.DoubleVar(value=1.0)
        self.colormap      = tk.StringVar(value="gray")
        self._data_range   = (0.0, 1.0)   # (data_min, data_max) of loaded image
        self._hist_clip_pct = self._load_hist_zoom()  # percentile clip for histogram X zoom (0 = full range)

        # --- View state ---
        self.zoom_level    = 1.0
        self.pan_x         = 0
        self.pan_y         = 0
        self._pan_start_pos    = None
        self._pan_start_offset = None

        # --- Sequence / video ---
        self.sequence_files: list[str] = []
        self.current_frame  = 0
        self.playing        = False
        self._play_after_id = None
        self._play_queue: queue.Queue = queue.Queue(maxsize=3)
        self._play_next_frame_time: float | None = None
        self._frame_slider_after_id = None
        self.frame_var      = tk.IntVar(value=0)

        # --- Background FITS loading (network shares can take a few seconds) ---
        self._load_queue: queue.Queue = queue.Queue()
        self._load_seq = 0   # request id -- lets a superseded load (e.g. rapid frame stepping) be discarded

        # --- Header panel ---
        self.header_visible = tk.BooleanVar(value=False)
        self._header_items: dict = {}   # iid -> [keyword, value, comment]

        # --- Histogram drag ---
        self._hist_dragging: str | None = None   # 'black' or 'white'
        self._level_after_id = None              # debounce id for slider changes
        self._resize_after_id = None             # debounce id for canvas resize
        self._cmap_lut_cache: dict = {}          # name → uint8 LUT (256×3)

        # --- Header window ---
        self.header_win = None

        # --- Aperture photometry ---
        _phot = self._load_phot_settings()
        self.aperture_center   = None            # (px, py) float image-pixel coords, or None
        self.aperture_radius   = tk.DoubleVar(value=_phot["ap_radius"])
        self.annulus_inner     = tk.DoubleVar(value=_phot["ap_inner"])
        self.annulus_outer     = tk.DoubleVar(value=_phot["ap_outer"])
        self.gain_eperadu      = tk.StringVar(value=_phot["ap_gain"])
        self.adu_scale         = tk.StringVar(value=_phot["ap_adu_scale"])   # e.g. "16" for a 12-bit ADC left-shifted into 16 bits; blank = unknown/unset
        self.full_well         = tk.StringVar(value=_phot["ap_full_well"])   # optional, electrons -- enables true saturation % on calibrated/float files
        self.target_electrons  = tk.StringVar(value=_phot["ap_target_electrons"])
        self.phot_visible      = tk.BooleanVar(value=False)
        self.phot_win          = None
        self._phot_press_pos   = None            # (px, py) anchor while dragging out a radius
        self._phot_dragged     = False           # True once the press turns into a real drag
        self._phot_result_vars: dict = {}        # label name -> StringVar, populated by panel
        self._phot_meter_label  = None            # the exposure-meter status Label widget, for color updates
        self._phot_meter_canvas = None            # the exposure-meter bar-gauge Canvas widget
        self._gain_source      = ""               # "FITS header (EGAIN)" or "" (manual/unset)
        self._adu_scale_source = ""               # "auto-detected" / "unverified (calibrated)" / "" (manual)
        self._setting_gain_programmatically = False
        self._setting_scale_programmatically = False
        self.gain_eperadu.trace_add("write", self._on_gain_var_written)
        self.adu_scale.trace_add("write", self._on_scale_var_written)

        self._build_ui()
        self._update_hist_zoom_label()   # show last-used zoom level immediately
        self._bind_keys()
        self.root.after(30, self._poll_load_fits)   # persistent drain loop for background FITS loads
        self.root.geometry("1080x776")    # 75% of 1440x1035 (which was itself 75% of the original 1920x1380)
        self.root.minsize(900, 675)       # lowered to match — minsize was clamping the window back up to 1200x900

    def _update_title(self, filename: str | None = None):
        if filename:
            self.root.title(f"Simple FITS Viewer  v{APP_VERSION}  —  {filename}")
        else:
            self.root.title(f"Simple FITS Viewer  v{APP_VERSION}")

    # -----------------------------------------------------------------------
    # UI Construction
    # -----------------------------------------------------------------------
    def _build_ui(self):
        self._build_menu()
        self._build_toolbar()

        # Horizontal paned window: image area | header panel
        self.main_pane = tk.PanedWindow(
            self.root, orient=tk.HORIZONTAL,
            bg=BG_COLOR, sashwidth=5, sashpad=1, sashrelief="flat"
        )
        self.main_pane.pack(fill="both", expand=True)

        # Left side: canvas + histogram + playback
        self.left_frame = tk.Frame(self.main_pane, bg=BG_COLOR)
        self.main_pane.add(self.left_frame, minsize=500)

        self._build_canvas()
        self._build_histogram()
        self._build_playback()

        self._build_statusbar()

    # --- Menu ---
    def _build_menu(self):
        mb_kw = dict(bg=NAV_COLOR, fg=NAV_FG, tearoff=False,
                     activebackground=ACCENT, activeforeground="white")
        item_kw = dict(bg=FRAME_COLOR, fg=TEXT_COLOR, tearoff=False,
                       activebackground=ACCENT, activeforeground="white")

        menubar = tk.Menu(self.root, **mb_kw)
        self.root.config(menu=menubar)

        # File
        fm = tk.Menu(menubar, **item_kw)
        menubar.add_cascade(label="File", menu=fm)
        fm.add_command(label="Open FITS…",           accelerator="Ctrl+O", command=self.open_file)
        fm.add_command(label="Open Directory (Sequence)…", accelerator="Ctrl+D", command=self.open_directory)
        fm.add_separator()
        fm.add_command(label="Exit",                 command=self.root.quit)

        # View
        vm = tk.Menu(menubar, **item_kw)
        menubar.add_cascade(label="View", menu=vm)
        vm.add_checkbutton(label="FITS Header Panel", variable=self.header_visible,
                           command=self.toggle_header)
        vm.add_checkbutton(label="Photometry Panel", variable=self.phot_visible,
                           command=self.toggle_photometry_panel)
        vm.add_separator()
        vm.add_command(label="Zoom In",      accelerator="+",  command=lambda: self.zoom(1.25))
        vm.add_command(label="Zoom Out",     accelerator="-",  command=lambda: self.zoom(0.8))
        vm.add_command(label="Fit to Window",accelerator="F",  command=self.zoom_fit)
        vm.add_command(label="1:1 Pixel",    accelerator="1",  command=lambda: self.set_zoom(1.0))

        # Image
        im = tk.Menu(menubar, **item_kw)
        menubar.add_cascade(label="Image", menu=im)
        im.add_command(label="Auto Stretch", accelerator="A", command=self.auto_stretch)
        im.add_command(label="Reset Pan",    command=self.reset_pan)

        # Help
        hm = tk.Menu(menubar, **item_kw)
        menubar.add_cascade(label="Help", menu=hm)
        hm.add_command(label="User Guide",                command=self._show_user_guide)
        hm.add_command(label="Revision History",          command=self._show_revision_history)
        hm.add_separator()
        hm.add_command(label="About Simple FITS Viewer…", command=self._show_about)

    # --- Toolbar ---
    def _build_toolbar(self):
        tb = tk.Frame(self.root, bg=NAV_COLOR, height=45)
        tb.pack(fill="x")
        tb.pack_propagate(False)

        def btn(parent, text, cmd):
            return tk.Button(
                parent, text=text, command=cmd,
                bg=NAV_COLOR, fg=NAV_FG, relief="flat", padx=8, pady=3,
                font=("Segoe UI", 11), cursor="hand2",
                activebackground="#2a5faa", activeforeground="white"
            )

        btn(tb, "Open",       self.open_file).pack(side="left", padx=2, pady=2)
        btn(tb, "Open Dir",   self.open_directory).pack(side="left", padx=2, pady=2)

        ttk.Separator(tb, orient="vertical").pack(side="left", fill="y", padx=6, pady=4)

        btn(tb, "Zoom In",    lambda: self.zoom(1.25)).pack(side="left", padx=2, pady=2)
        btn(tb, "Zoom Out",   lambda: self.zoom(0.8)).pack(side="left", padx=2, pady=2)
        btn(tb, "Fit",        self.zoom_fit).pack(side="left", padx=2, pady=2)
        btn(tb, "1:1",        lambda: self.set_zoom(1.0)).pack(side="left", padx=2, pady=2)

        ttk.Separator(tb, orient="vertical").pack(side="left", fill="y", padx=6, pady=4)

        tk.Label(tb, text="Stretch:", bg=NAV_COLOR, fg=NAV_FG,
                 font=("Segoe UI", 11)).pack(side="left", padx=(2, 1))
        stretch_cb = ttk.Combobox(tb, textvariable=self.stretch_mode,
                                   values=STRETCH_MODES, width=14, state="readonly")
        stretch_cb.pack(side="left", padx=2, pady=3)
        stretch_cb.bind("<<ComboboxSelected>>", lambda e: self.auto_stretch())

        tk.Label(tb, text="Colormap:", bg=NAV_COLOR, fg=NAV_FG,
                 font=("Segoe UI", 11)).pack(side="left", padx=(6, 1))
        cmap_cb = ttk.Combobox(tb, textvariable=self.colormap,
                                values=COLORMAPS, width=10, state="readonly")
        cmap_cb.pack(side="left", padx=2, pady=3)
        cmap_cb.bind("<<ComboboxSelected>>", lambda e: self.apply_stretch())

        ttk.Separator(tb, orient="vertical").pack(side="left", fill="y", padx=6, pady=4)

        btn(tb, "Header",     self._toolbar_toggle_header).pack(side="left", padx=2, pady=2)
        btn(tb, "Photometry", self._toolbar_toggle_photometry).pack(side="left", padx=2, pady=2)

        ttk.Separator(tb, orient="vertical").pack(side="left", fill="y", padx=6, pady=4)

        self.tb_pixel = tk.Label(tb, text="", bg=NAV_COLOR, fg=NAV_FG,
                                 font=("Segoe UI", 11), anchor="w")
        self.tb_pixel.pack(side="left", padx=4)


    # --- Canvas ---
    def _build_canvas(self):
        cf = tk.Frame(self.left_frame, bg="black")
        cf.pack(fill="both", expand=True)

        self.canvas = tk.Canvas(cf, bg="#0a0a0f", cursor="crosshair",
                                highlightthickness=0)
        hsb = ttk.Scrollbar(cf, orient="horizontal", command=self.canvas.xview)
        vsb = ttk.Scrollbar(cf, orient="vertical",   command=self.canvas.yview)
        self.canvas.configure(xscrollcommand=hsb.set, yscrollcommand=vsb.set)

        hsb.pack(side="bottom", fill="x")
        vsb.pack(side="right",  fill="y")
        self.canvas.pack(fill="both", expand=True)

        self._placeholder = self.canvas.create_text(
            500, 400,
            text="Open a FITS file to begin\n(File > Open  or  Ctrl+O)"
                 "\n\nTo view a sequence, open a directory containing FITS files\n(File > Open Directory  or  Ctrl+D)",
            fill="#444466", font=("Segoe UI", 14), anchor="center",
            tags="placeholder"
        )

        # Mouse bindings
        self.canvas.bind("<MouseWheel>",       self._on_mousewheel)
        self.canvas.bind("<Button-4>",         lambda e: self.zoom(1.1))
        self.canvas.bind("<Button-5>",         lambda e: self.zoom(0.9))
        self.canvas.bind("<ButtonPress-2>",    self._pan_press)
        self.canvas.bind("<B2-Motion>",        self._pan_drag)
        self.canvas.bind("<ButtonPress-3>",    self._pan_press)
        self.canvas.bind("<B3-Motion>",        self._pan_drag)
        self.canvas.bind("<Motion>",           self._on_canvas_motion)
        self.canvas.bind("<Configure>",        self._on_canvas_resize)
        self.canvas.bind("<ButtonPress-1>",    self._phot_press)
        self.canvas.bind("<B1-Motion>",        self._phot_motion)
        self.canvas.bind("<ButtonRelease-1>",  self._phot_release)

    # --- Histogram ---
    def _build_histogram(self):
        hf = tk.Frame(self.left_frame, bg=FRAME_COLOR, height=270)
        hf.pack(fill="x")
        hf.pack_propagate(False)

        # Matplotlib figure embedded in tk
        self.hist_fig = Figure(figsize=(5, 1.3), dpi=96, facecolor="#12121e")
        self.hist_ax  = self.hist_fig.add_subplot(111)
        self.hist_ax.set_facecolor("#12121e")
        self.hist_fig.subplots_adjust(left=0.04, right=0.99, top=0.88, bottom=0.18)

        self.hist_canvas_widget = FigureCanvasTkAgg(self.hist_fig, master=hf)
        self.hist_canvas_widget.get_tk_widget().pack(side="left", fill="both", expand=True)

        # Connect histogram mouse events for dragging level lines
        self.hist_canvas_widget.mpl_connect("button_press_event",   self._hist_press)
        self.hist_canvas_widget.mpl_connect("motion_notify_event",  self._hist_motion)
        self.hist_canvas_widget.mpl_connect("button_release_event", self._hist_release)

        self._black_vline = None
        self._white_vline = None

        # Level controls
        ctrl = tk.Frame(hf, bg=FRAME_COLOR, width=280)
        ctrl.pack(side="right", fill="y", padx=6)
        ctrl.pack_propagate(False)

        lbl  = dict(bg=FRAME_COLOR, fg=TEXT_COLOR, font=("Segoe UI", 12))
        sldr = dict(bg=FRAME_COLOR, fg=TEXT_COLOR, troughcolor="#2a2a48",
                    highlightthickness=0, length=160, relief="flat")
        pm   = dict(bg="#1a3060", fg="white", relief="flat", font=("Segoe UI", 13, "bold"),
                    cursor="hand2", width=2, pady=1)

        tk.Label(ctrl, text="Black Level", **lbl).pack(pady=(8, 0))
        blk_row = tk.Frame(ctrl, bg=FRAME_COLOR)
        blk_row.pack()
        tk.Button(blk_row, text="−", command=lambda: self._bump_level("black", -0.001), **pm).pack(side="left")
        self.black_slider = tk.Scale(blk_row, variable=self.black_level,
                                      from_=0.0, to=1.0, resolution=0.001,
                                      orient="horizontal", showvalue=True, digits=4,
                                      command=lambda v: self._on_level_changed(), **sldr)
        self.black_slider.pack(side="left")
        tk.Button(blk_row, text="+", command=lambda: self._bump_level("black", +0.001), **pm).pack(side="left")
        self._bind_slider_trough(self.black_slider, "black")

        tk.Label(ctrl, text="White Level", **lbl).pack(pady=(6, 0))
        wht_row = tk.Frame(ctrl, bg=FRAME_COLOR)
        wht_row.pack()
        tk.Button(wht_row, text="−", command=lambda: self._bump_level("white", -0.001), **pm).pack(side="left")
        self.white_slider = tk.Scale(wht_row, variable=self.white_level,
                                      from_=0.0, to=1.0, resolution=0.001,
                                      orient="horizontal", showvalue=True, digits=4,
                                      command=lambda v: self._on_level_changed(), **sldr)
        self.white_slider.pack(side="left")
        self.white_slider.set(1.0)
        tk.Button(wht_row, text="+", command=lambda: self._bump_level("white", +0.001), **pm).pack(side="left")
        self._bind_slider_trough(self.white_slider, "white")

        tk.Button(ctrl, text="Auto Stretch", command=self.auto_stretch,
                  bg="#1a5080", fg="white", relief="flat",
                  font=("Segoe UI", 12), cursor="hand2", pady=3).pack(pady=6, fill="x")

        # Histogram X-zoom control
        zoom_row = tk.Frame(ctrl, bg=FRAME_COLOR)
        zoom_row.pack(pady=(0, 4))
        tk.Label(zoom_row, text="Hist Zoom:", **lbl).pack(side="left", padx=(0, 4))
        tk.Button(zoom_row, text="−", command=self._hist_zoom_out, **pm).pack(side="left")
        self._hist_zoom_lbl = tk.Label(zoom_row, text="", width=5, **lbl)
        self._hist_zoom_lbl.pack(side="left")
        tk.Button(zoom_row, text="+", command=self._hist_zoom_in,  **pm).pack(side="left")

    # --- Playback controls ---
    def _build_playback(self):
        pb = tk.Frame(self.left_frame, bg=FRAME_COLOR, height=114)
        pb.pack(fill="x")
        pb.pack_propagate(False)

        bs = dict(bg=FRAME_COLOR, fg=TEXT_COLOR, relief="flat", padx=18,
                  font=("Segoe UI", 22), cursor="hand2",
                  activebackground="#333355", activeforeground="white")

        tk.Button(pb, text="⏮", command=self.frame_first, **bs).pack(side="left", padx=6, pady=12)
        tk.Button(pb, text="|◀", command=self.frame_prev,  **bs).pack(side="left", padx=6, pady=12)
        self.play_btn = tk.Button(pb, text="▶", command=self.toggle_play, **bs)
        self.play_btn.pack(side="left", padx=6, pady=12)
        tk.Button(pb, text="▶|", command=self.frame_next,  **bs).pack(side="left", padx=6, pady=12)
        tk.Button(pb, text="⏭", command=self.frame_last,   **bs).pack(side="left", padx=6, pady=12)

        ttk.Separator(pb, orient="vertical").pack(side="left", fill="y", padx=18, pady=18)

        self.frame_label = tk.Label(pb, text="Frame: -/-", bg=FRAME_COLOR, fg=TEXT_COLOR,
                                     font=("Segoe UI", 18), width=14)
        self.frame_label.pack(side="left", padx=12)

        self.frame_slider = tk.Scale(pb, variable=self.frame_var, from_=0, to=0,
                                      orient="horizontal", showvalue=False,
                                      bg=FRAME_COLOR, fg=TEXT_COLOR, troughcolor="#2a2a48",
                                      highlightthickness=0, length=660, relief="flat",
                                      command=self._on_frame_slider)
        self.frame_slider.pack(side="left", padx=12, pady=12)

        ttk.Separator(pb, orient="vertical").pack(side="left", fill="y", padx=18, pady=18)


    # --- Header panel (independent Toplevel) ---
    def _create_header_window(self):
        self.header_win = tk.Toplevel(self.root)
        self.header_win.title("FITS Header")
        self.header_win.geometry("336x650")   # 50% of the previous 672x1300
        self.header_win.configure(bg=FRAME_COLOR)
        self.header_win.protocol("WM_DELETE_WINDOW", self._on_header_win_close)

        tk.Label(self.header_win, text="FITS Header", bg=NAV_COLOR, fg=NAV_FG,
                 font=("Segoe UI", 10, "bold"), pady=6).pack(fill="x")

        sf = tk.Frame(self.header_win, bg=FRAME_COLOR)
        sf.pack(fill="x", padx=4, pady=(4, 0))
        tk.Label(sf, text="Search:", bg=FRAME_COLOR, fg=TEXT_COLOR,
                 font=("Segoe UI", 8)).pack(side="left")
        self.header_search = tk.StringVar()
        tk.Entry(sf, textvariable=self.header_search, bg="#1e1e30", fg=TEXT_COLOR,
                 insertbackground=TEXT_COLOR, relief="flat",
                 font=("Segoe UI", 9)).pack(side="left", fill="x", expand=True, padx=4)
        self.header_search.trace_add("write", lambda *_: self._filter_header())

        tk.Label(self.header_win,
                 text="Double-click a Keyword, Value, or Comment cell to edit it.",
                 bg=FRAME_COLOR, fg="#6666aa", font=("Segoe UI", 8, "italic"),
                 anchor="w").pack(fill="x", padx=6, pady=(2, 0))

        tf = tk.Frame(self.header_win, bg=FRAME_COLOR)
        tf.pack(fill="both", expand=True, padx=4, pady=4)

        style = ttk.Style()
        style.configure("Header.Treeview", background="#1a1a2e", foreground=TEXT_COLOR,
                         fieldbackground="#1a1a2e", rowheight=30)
        style.configure("Header.Treeview.Heading", background=FRAME_COLOR, foreground=TEXT_COLOR)
        style.map("Header.Treeview", background=[("selected", "#2a4a7f")])

        cols = ("Keyword", "Value", "Comment")
        self.header_tree = ttk.Treeview(tf, columns=cols, show="headings",
                                         selectmode="browse", style="Header.Treeview")
        for col in cols:
            self.header_tree.heading(col, text=col)
        self.header_tree.column("Keyword", width=80,  minwidth=60, stretch=False)
        self.header_tree.column("Value",   width=110, minwidth=80, stretch=False)
        self.header_tree.column("Comment", width=180, minwidth=100)

        vsb = ttk.Scrollbar(tf, orient="vertical", command=self.header_tree.yview)
        hsb = ttk.Scrollbar(tf, orient="horizontal", command=self.header_tree.xview)
        self.header_tree.configure(yscrollcommand=vsb.set, xscrollcommand=hsb.set)
        vsb.pack(side="right", fill="y")
        hsb.pack(side="bottom", fill="x")
        self.header_tree.pack(fill="both", expand=True)

        self.header_tree.bind("<Double-1>", self._on_header_double_click)

        bf = tk.Frame(self.header_win, bg=FRAME_COLOR)
        bf.pack(fill="x", padx=4, pady=(0, 6))
        bs = dict(relief="flat", font=("Segoe UI", 8), cursor="hand2", padx=8, pady=3)
        tk.Button(bf, text="Add Keyword",    bg="#1a5080", fg="white",
                  command=self._add_header_card,    **bs).pack(side="left", padx=2)
        tk.Button(bf, text="Delete Keyword", bg="#6a1a1a", fg="white",
                  command=self._delete_header_card, **bs).pack(side="left", padx=2)
        tk.Button(bf, text="Save to File", bg="#1a6a30", fg="white",
                  command=self.save_header,          **bs).pack(side="right", padx=2)

    def _on_header_win_close(self):
        self.header_visible.set(False)
        self.header_win.withdraw()

    # --- Photometry panel (independent Toplevel) ---
    def _toolbar_toggle_photometry(self):
        # Always show — only the window's X button closes it
        self.phot_visible.set(True)
        self.toggle_photometry_panel()

    def toggle_photometry_panel(self):
        if self.phot_visible.get():
            if self.phot_win is None:
                self._create_photometry_window()
            else:
                self.phot_win.deiconify()
        else:
            if self.phot_win is not None:
                self.phot_win.withdraw()

    def _on_phot_win_close(self):
        self.phot_visible.set(False)
        self.phot_win.withdraw()
        self._reset_aperture()

    def _help_icon(self, parent, title: str, body: str) -> tk.Label:
        """Small clickable '[?]' that pops up a short modal explanation --
        the same inline-help pattern used in TransitLab/TransitFinder.
        """
        icon = tk.Label(parent, text="[?]", bg=FRAME_COLOR, fg=ACCENT,
                         font=("Segoe UI", 8, "bold"), cursor="hand2")
        icon.bind("<Button-1>", lambda e: self._show_help_popup(title, body))
        return icon

    def _show_help_popup(self, title: str, body: str):
        win = tk.Toplevel(self.root)
        win.title(title)
        win.configure(bg=FRAME_COLOR)
        win.resizable(False, False)
        win.attributes("-topmost", True)
        tk.Label(win, text=title, bg=NAV_COLOR, fg=NAV_FG,
                 font=("Segoe UI", 10, "bold"), pady=6, wraplength=360).pack(fill="x")
        tk.Label(win, text=body, bg=FRAME_COLOR, fg=TEXT_COLOR,
                 font=("Segoe UI", 9), wraplength=340, justify="left",
                 padx=14, pady=12).pack(fill="both", expand=True)
        tk.Button(win, text="Close", command=win.destroy,
                  bg="#1a3060", fg="white", relief="flat",
                  font=("Segoe UI", 9), cursor="hand2", padx=12, pady=4
                  ).pack(pady=(0, 10))
        win.update_idletasks()
        if self.phot_win is not None:
            x = self.phot_win.winfo_x() + 20
            y = self.phot_win.winfo_y() + 20
            win.geometry(f"+{x}+{y}")
        win.bind("<Escape>", lambda e: win.destroy())

    def _create_photometry_window(self):
        self.phot_win = tk.Toplevel(self.root)
        self.phot_win.title("Aperture Photometry")
        self.phot_win.geometry("380x900")
        self.phot_win.configure(bg=FRAME_COLOR)
        self.phot_win.protocol("WM_DELETE_WINDOW", self._on_phot_win_close)
        self.phot_win.attributes("-topmost", True)   # stays floating above the main window

        tk.Label(self.phot_win, text="Aperture Photometry", bg=NAV_COLOR, fg=NAV_FG,
                 font=("Segoe UI", 10, "bold"), pady=6).pack(fill="x")
        tk.Label(self.phot_win,
                 text="Click a star to auto-size the aperture around it, "
                      "or click-and-drag to size it manually.",
                 bg=FRAME_COLOR, fg="#8888bb", font=("Segoe UI", 8, "italic"),
                 wraplength=340, justify="left", anchor="w").pack(fill="x", padx=8, pady=(6, 4))

        tk.Button(self.phot_win, text="Clear Aperture", command=self._reset_aperture,
                  bg="#6a1a1a", fg="white", relief="flat", font=("Segoe UI", 9),
                  cursor="hand2", pady=3).pack(fill="x", padx=8, pady=(0, 4))

        lbl = dict(bg=FRAME_COLOR, fg=TEXT_COLOR, font=("Segoe UI", 9))

        def spin_row(parent, label, var, frm, to, incr):
            row = tk.Frame(parent, bg=FRAME_COLOR)
            row.pack(fill="x", padx=8, pady=2)
            tk.Label(row, text=label, width=16, anchor="w", **lbl).pack(side="left")
            sb = tk.Spinbox(row, from_=frm, to=to, increment=incr, textvariable=var,
                             width=8, bg="#1e1e30", fg=TEXT_COLOR, insertbackground=TEXT_COLOR,
                             buttonbackground=FRAME_COLOR, relief="flat",
                             command=self._on_phot_param_changed)
            sb.pack(side="left")
            sb.bind("<Return>",   lambda e: self._on_phot_param_changed())
            sb.bind("<FocusOut>", lambda e: self._on_phot_param_changed())
            return sb

        def entry_row(parent, label, var, width=10, help=None):
            row = tk.Frame(parent, bg=FRAME_COLOR)
            row.pack(fill="x", padx=8, pady=2)
            tk.Label(row, text=label, width=16, anchor="w", **lbl).pack(side="left")
            ent = tk.Entry(row, textvariable=var, width=width,
                            bg="#1e1e30", fg=TEXT_COLOR, insertbackground=TEXT_COLOR,
                            relief="flat")
            ent.pack(side="left")
            ent.bind("<Return>",   lambda e: self._on_phot_param_changed())
            ent.bind("<FocusOut>", lambda e: self._on_phot_param_changed())
            if help is not None:
                self._help_icon(row, *help).pack(side="left", padx=(6, 0))
            return ent

        tk.Frame(self.phot_win, bg=SEP_COLOR, height=1).pack(fill="x", padx=8, pady=(2, 6))
        tk.Label(self.phot_win, text="Geometry (pixels)", bg=FRAME_COLOR, fg=ACCENT,
                 font=("Segoe UI", 9, "bold"), anchor="w").pack(fill="x", padx=8)
        spin_row(self.phot_win, "Aperture radius",  self.aperture_radius, 1, 500, 1)
        spin_row(self.phot_win, "Sky annulus inner", self.annulus_inner,   1, 800, 1)
        spin_row(self.phot_win, "Sky annulus outer", self.annulus_outer,   1, 900, 1)

        tk.Frame(self.phot_win, bg=SEP_COLOR, height=1).pack(fill="x", padx=8, pady=(8, 6))
        tk.Label(self.phot_win, text="Gain & Scaling", bg=FRAME_COLOR, fg=ACCENT,
                 font=("Segoe UI", 9, "bold"), anchor="w").pack(fill="x", padx=8)

        entry_row(self.phot_win, "Gain (e-/ADU)",     self.gain_eperadu, help=("Gain (e-/ADU)",
            "Electrons collected per ADU count -- converts raw pixel values into "
            "electrons for Total electrons and the Exposure Meter.\n\n"
            "Auto-filled from the FITS header's EGAIN keyword when present "
            "('Gain source' below shows where it came from). Type over it "
            "manually at any time."))
        entry_row(self.phot_win, "ADU scale",         self.adu_scale, help=("ADU Scale",
            "Some cameras pack a lower-bit-depth ADC reading (e.g. 12-bit) into a "
            "wider 16-bit file by left-shifting it, so every pixel value ends up "
            "an exact multiple of a power of two (commonly 16). This divides that "
            "back out before Gain is applied, so Total electrons is correct even "
            "though Raw/Sky-sub/Peak ADU show the literal stored values.\n\n"
            "Auto-detected reliably on raw sensor files. On calibrated/float "
            "files, dark subtraction and flat division destroy that exact "
            "pattern, so detection is unreliable -- the field is left as-is "
            "instead of silently assuming no scale is needed. If it shows "
            "'unverified', type in the value you see on a raw frame shot with "
            "the same camera and gain; otherwise Total electrons can be off by "
            "that same factor."))
        entry_row(self.phot_win, "Full well (e-)",    self.full_well, help=("Full Well (e-)",
            "Optional. Your camera's per-pixel electron capacity at this gain "
            "setting. Not available from the FITS header (that spec is "
            "essentially never present) or a camera database -- you supply it "
            "once per camera/gain combination.\n\n"
            "When set, the Exposure Meter's saturation check uses true electrons "
            "and works on calibrated/float files too. Left blank, saturation "
            "falls back to a proxy based on the file's bit depth (the ADC "
            "ceiling), which only works on raw integer files."))
        entry_row(self.phot_win, "Target electrons",  self.target_electrons, help=("Target Electrons",
            "The total aperture-summed electron count you're aiming for per "
            "exposure, default 100,000.\n\n"
            "Past this point, photon noise from the target star is usually no "
            "longer the dominant source of scatter in ground-based differential "
            "photometry -- scintillation, flat-fielding, and guiding drift tend "
            "to dominate instead, so collecting more electrons just costs cadence "
            "without buying real precision.\n\n"
            "Not a hard rule -- lower it for a high-scintillation site, raise it "
            "for a shallow transit or where tighter per-point timing matters "
            "more than a few extra frames."))
        tk.Label(self.phot_win,
                 text="ADU scale: leave as auto-detected for raw sensor files. On "
                      "calibrated/float files, detection is unreliable -- type in "
                      "the value from a raw frame shot with the same camera/gain.\n"
                      "Full well: optional. Without it, saturation is judged from "
                      "the file's bit depth alone, which calibrated files don't have.",
                 bg=FRAME_COLOR, fg="#8888bb", font=("Segoe UI", 8, "italic"),
                 wraplength=340, justify="left", anchor="w").pack(fill="x", padx=8, pady=(2, 0))

        tk.Frame(self.phot_win, bg=SEP_COLOR, height=1).pack(fill="x", padx=8, pady=(10, 6))
        tk.Label(self.phot_win, text="Results", bg=FRAME_COLOR, fg=ACCENT,
                 font=("Segoe UI", 9, "bold"), anchor="w").pack(fill="x", padx=8)

        results = tk.Frame(self.phot_win, bg=FRAME_COLOR)
        results.pack(fill="x", padx=8, pady=(2, 8))

        def result_row(label, key):
            row = tk.Frame(results, bg=FRAME_COLOR)
            row.pack(fill="x", pady=1)
            tk.Label(row, text=label, width=16, anchor="w", **lbl).pack(side="left")
            var = tk.StringVar(value="—")
            tk.Label(row, textvariable=var, anchor="w",
                     bg=FRAME_COLOR, fg="white", font=("Segoe UI", 9, "bold")).pack(side="left")
            self._phot_result_vars[key] = var

        result_row("Center (X, Y)",    "center")
        result_row("Aperture pixels",  "n_ap")
        result_row("Sky median/px",    "sky_median")
        result_row("Sky σ",            "sky_sigma")
        result_row("Raw sum (ADU)",    "raw_sum")
        result_row("Sky-sub sum (ADU)","sub_sum")
        result_row("Peak (ADU)",       "peak")
        result_row("ADU scale",        "divisor")
        result_row("Gain source",      "gain_source")
        result_row("Total electrons",  "electrons")

        tk.Frame(self.phot_win, bg=SEP_COLOR, height=1).pack(fill="x", padx=8, pady=(4, 6))
        meter_header = tk.Frame(self.phot_win, bg=FRAME_COLOR)
        meter_header.pack(fill="x", padx=8)
        tk.Label(meter_header, text="Exposure Meter", bg=FRAME_COLOR, fg=ACCENT,
                 font=("Segoe UI", 9, "bold"), anchor="w").pack(side="left")
        self._help_icon(meter_header, "Exposure Meter",
            "A combined read on this exposure, replacing the old Level readout.\n\n"
            "Signal bar -- Total electrons against your Target: amber zone is "
            "under-target, green is the good band, blue is over-target. The "
            "white marker shows where you currently land.\n\n"
            "Saturation bar -- the peak pixel against Full well (or the "
            "ADC-ceiling proxy if Full well isn't set): green up to 70%, amber "
            "70-85%, red above 85%.\n\n"
            "Status line -- one of five states, highest priority first: "
            "NEAR SATURATION, TOO FAINT, LOW SIGNAL, EXCESS SIGNAL, GOOD.\n\n"
            "Recommendation -- plain 'increase/reduce/no change' guidance, with "
            "a suggested new exposure time when the FITS header has EXPTIME or "
            "EXPOSURE."
        ).pack(side="left", padx=(6, 0))

        meter_frame = tk.Frame(self.phot_win, bg=FRAME_COLOR)
        meter_frame.pack(fill="x", padx=8, pady=(2, 8))

        self._phot_meter_canvas = tk.Canvas(
            meter_frame, width=self._METER_PAD * 2 + self._METER_BAR_W, height=76,
            bg=FRAME_COLOR, highlightthickness=0)
        self._phot_meter_canvas.pack(fill="x", pady=(0, 4))

        state_row = tk.Frame(meter_frame, bg=FRAME_COLOR)
        state_row.pack(fill="x", pady=1)
        tk.Label(state_row, text="Status", width=16, anchor="w", **lbl).pack(side="left")
        meter_state_var = tk.StringVar(value="—")
        self._phot_result_vars["meter_state"] = meter_state_var
        self._phot_meter_label = tk.Label(state_row, textvariable=meter_state_var, anchor="w",
                                           bg=FRAME_COLOR, fg="white", font=("Segoe UI", 10, "bold"))
        self._phot_meter_label.pack(side="left")

        rec_row = tk.Frame(meter_frame, bg=FRAME_COLOR)
        rec_row.pack(fill="x", pady=(3, 1))
        meter_rec_var = tk.StringVar(value="")
        self._phot_result_vars["meter_rec"] = meter_rec_var
        tk.Label(rec_row, textvariable=meter_rec_var, anchor="w", justify="left",
                 wraplength=340, bg=FRAME_COLOR, fg=TEXT_COLOR,
                 font=("Segoe UI", 9)).pack(side="left")

        # Reflect current results (if an aperture already exists) or blank state
        self._update_photometry_results(None)
        if self.aperture_center is not None:
            self._compute_photometry()

    def _on_phot_param_changed(self):
        try:
            float(self.aperture_radius.get())
            float(self.annulus_inner.get())
            float(self.annulus_outer.get())
        except (ValueError, tk.TclError):
            return
        self._draw_aperture_overlay()
        self._compute_photometry()
        self._save_phot_settings()

    def _update_photometry_results(self, result: dict | None):
        if not self._phot_result_vars:
            return
        v = self._phot_result_vars
        if result is None:
            for var in v.values():
                var.set("—")
            if self._phot_meter_label is not None:
                self._phot_meter_label.config(fg=TEXT_COLOR)
            self._draw_exposure_meter(None)
            return
        v["center"].set(f"{result['cx']:.1f}, {result['cy']:.1f}")
        v["n_ap"].set(f"{result['n_ap']}")
        v["sky_median"].set(f"{result['sky_median']:.3f}")
        v["sky_sigma"].set(f"{result['sky_sigma']:.3f}")
        v["raw_sum"].set(f"{result['raw_sum']:.2f}")
        v["sub_sum"].set(f"{result['sub_sum']:.2f}")
        v["peak"].set(f"{result['peak']:.2f}")
        divisor = result.get("divisor", 1)
        scale_source = result.get("adu_scale_source") or "manual entry"
        v["divisor"].set(f"÷{divisor:g}  ({scale_source})" if divisor != 1 else f"none  ({scale_source})")
        v["gain_source"].set(result.get("gain_source") or "manual entry")
        if result["electrons"] is not None:
            v["electrons"].set(f"{result['electrons']:.1f}")
        else:
            v["electrons"].set("— (set gain)")

        meter = result.get("meter") or {}
        state  = meter.get("state", "—")
        detail = meter.get("detail", "")
        v["meter_state"].set(f"{state}  ({detail})" if detail else state)
        v["meter_rec"].set(meter.get("recommendation", ""))
        if self._phot_meter_label is not None:
            self._phot_meter_label.config(fg=meter.get("color", TEXT_COLOR))
        self._draw_exposure_meter(meter)

    # --- Photometry settings persistence ---
    def _load_phot_settings(self) -> dict:
        defaults = {
            "ap_radius": 12.0, "ap_inner": 18.0, "ap_outer": 26.0, "ap_gain": "",
            "ap_adu_scale": "", "ap_full_well": "", "ap_target_electrons": "100000",
        }
        try:
            with open(_SETTINGS_PATH, "r") as f:
                d = json.load(f)
            return {
                "ap_radius": float(d.get("ap_radius", defaults["ap_radius"])),
                "ap_inner":  float(d.get("ap_inner",  defaults["ap_inner"])),
                "ap_outer":  float(d.get("ap_outer",  defaults["ap_outer"])),
                "ap_gain":   str(d.get("ap_gain",     defaults["ap_gain"])),
                "ap_adu_scale":        str(d.get("ap_adu_scale",        defaults["ap_adu_scale"])),
                "ap_full_well":        str(d.get("ap_full_well",        defaults["ap_full_well"])),
                "ap_target_electrons": str(d.get("ap_target_electrons", defaults["ap_target_electrons"])),
            }
        except Exception:
            return defaults

    def _save_phot_settings(self):
        try:
            data = {}
            if _SETTINGS_PATH.exists():
                with open(_SETTINGS_PATH, "r") as f:
                    data = json.load(f)
            data["ap_radius"] = self.aperture_radius.get()
            data["ap_inner"]  = self.annulus_inner.get()
            data["ap_outer"]  = self.annulus_outer.get()
            data["ap_gain"]   = self.gain_eperadu.get()
            data["ap_adu_scale"]        = self.adu_scale.get()
            data["ap_full_well"]        = self.full_well.get()
            data["ap_target_electrons"] = self.target_electrons.get()
            with open(_SETTINGS_PATH, "w") as f:
                json.dump(data, f)
        except Exception:
            pass

    # --- User Guide ---
    def _show_user_guide(self):
        win = tk.Toplevel(self.root)
        win.title("User Guide — Simple FITS Viewer")
        win.configure(bg=FRAME_COLOR)
        win.resizable(True, True)
        win.geometry("860x900")

        tk.Label(win, text="Simple FITS Viewer — User Guide",
                 bg=NAV_COLOR, fg=NAV_FG, font=("Segoe UI", 11, "bold"),
                 pady=8).pack(fill="x")

        tf = tk.Frame(win, bg=FRAME_COLOR)
        tf.pack(fill="both", expand=True, padx=8, pady=8)

        txt = tk.Text(tf, bg="#1a1a2e", fg=TEXT_COLOR, font=("Courier New", 13),
                      relief="flat", wrap="none", cursor="arrow",
                      state="normal", padx=10, pady=8)
        vsb = ttk.Scrollbar(tf, orient="vertical", command=txt.yview)
        txt.configure(yscrollcommand=vsb.set)
        vsb.pack(side="right", fill="y")
        txt.pack(fill="both", expand=True)

        txt.tag_configure("heading",  foreground="#ffffff", font=("Courier New", 13, "bold"))
        txt.tag_configure("toc_link", foreground=ACCENT,   font=("Courier New", 13), underline=True)
        txt.tag_configure("rule",     foreground=SEP_COLOR)

        sections = [
            ("1.  Opening Files",         "sec_open"),
            ("2.  Zoom & Pan",            "sec_zoom"),
            ("3.  Stretch & Color",       "sec_stretch"),
            ("4.  Histogram",             "sec_hist"),
            ("5.  Sequence Playback",     "sec_seq"),
            ("6.  Cursor Readout",        "sec_cursor"),
            ("7.  FITS Header",           "sec_header"),
            ("8.  Aperture Photometry",   "sec_phot"),
        ]

        # Table of contents
        txt.insert("end", "TABLE OF CONTENTS\n", "heading")
        txt.insert("end", "─" * 52 + "\n", "rule")
        for label, mark in sections:
            tag = f"toc_{mark}"
            txt.insert("end", f"  {label}\n", tag)
            txt.tag_configure(tag, foreground=ACCENT, font=("Courier New", 13), underline=True)
            txt.tag_bind(tag, "<Button-1>", lambda e, m=mark: txt.see(m))
            txt.tag_bind(tag, "<Enter>",    lambda e: txt.config(cursor="hand2"))
            txt.tag_bind(tag, "<Leave>",    lambda e: txt.config(cursor="arrow"))
        txt.insert("end", "\n")

        def section(title, mark, lines):
            txt.mark_set(mark, "end")
            txt.mark_gravity(mark, "left")
            txt.insert("end", title + "\n", "heading")
            txt.insert("end", "─" * 52 + "\n", "rule")
            txt.insert("end", lines + "\n\n")

        section("1.  OPENING FILES", "sec_open",
            "  • Open File (Ctrl+O)       — load a single FITS image\n"
            "  • Open Directory (Ctrl+D)  — load a folder as an image sequence\n"
            "  • Double-click a .fits     — open directly from Explorer\n"
            "  • The window title bar and status bar both show the open file's name")

        section("2.  ZOOM & PAN  (image canvas)", "sec_zoom",
            "  • Scroll wheel             — zoom in / out, staying centered\n"
            "      on the cursor position\n"
            "  • Right-drag or mid-drag   — pan\n"
            "  • F                        — fit image to window\n"
            "  • 1                        — 1:1 pixel zoom\n"
            "  • +  /  −  (keyboard)      — zoom in / out\n"
            "  • Zoom In / Out buttons    — same as keyboard")

        section("3.  STRETCH & COLOR", "sec_stretch",
            "  • Stretch dropdown         — ZScale, Percentile 99.5%,\n"
            "                               Percentile 98%, MinMax, Linear 5-99%\n"
            "  • Colormap dropdown        — gray, viridis, plasma, inferno, etc.\n"
            "  • Auto Stretch button      — recompute levels for current mode\n"
            "  • A  (keyboard)            — same as Auto Stretch\n"
            "  • Black / White sliders    — fine-tune display range\n"
            "      +/− buttons = 1 step   trough click = 5 steps\n"
            "  • Drag red / yellow lines  — adjust levels on histogram directly")

        section("4.  HISTOGRAM", "sec_hist",
            "  • Red dashed line          — black (shadow) level\n"
            "  • Yellow dashed line       — white (highlight) level\n"
            "  • Hist Zoom  − / +         — widen or narrow the histogram X axis\n"
            "      Full → 0.01% → 0.02% → 0.05% → 0.1% → 0.2% → 0.5% → …\n"
            "      (clip % trimmed from each end of the data range)")

        section("5.  SEQUENCE PLAYBACK", "sec_seq",
            "  • ⏮   — go to first frame\n"
            "  • |◀   — step back one frame\n"
            "  • ▶ / ⏸  — play / pause at ~1 fps\n"
            "  • ▶|   — step forward one frame\n"
            "  • ⏭   — go to last frame\n"
            "  • Frame slider             — drag to scrub to any frame\n"
            "  • Space                    — play / pause\n"
            "  • ← / →  (keyboard)        — step one frame")

        section("6.  CURSOR READOUT  (top toolbar)", "sec_cursor",
            "  • X, Y                     — pixel coordinates under the cursor\n"
            "  • ADU                      — raw pixel value at that position\n"
            "  • RA, Dec                  — sky coordinates (plate-solved images only)")

        section("7.  FITS HEADER", "sec_header",
            "  • Header button            — open the header viewer / editor\n"
            "  • Double-click any cell    — edit Keyword, Value, or Comment\n"
            "  • Add Keyword              — append a new header card\n"
            "  • Delete Keyword           — remove selected card\n"
            "  • Save Header              — write changes back to the FITS file\n"
            "  • Window X button          — close the header panel")

        section("8.  APERTURE PHOTOMETRY", "sec_phot",
            "  • Photometry button         — open the Aperture Photometry panel.\n"
            "      Stays floating on top of the main window until you close it,\n"
            "      and also opens automatically the first time you click to\n"
            "      place an aperture\n"
            "  • Left-click on a star      — centroids on it and auto-sizes the\n"
            "      aperture/annulus from its FWHM (radial half-max profile).\n"
            "      Clicking blank sky just re-centers, keeping the current radius\n"
            "  • Left-click + drag         — place center and drag out a radius\n"
            "      manually, overriding auto-sizing for this aperture\n"
            "  • Geometry spinboxes        — fine-tune radius/annulus afterward,\n"
            "      whether it was auto-sized or dragged manually\n"
            "  • Aperture radius           — green circle: the star aperture\n"
            "  • Sky annulus inner/outer   — cyan/magenta circles: background ring\n"
            "      Sky level is the sigma-clipped median ADU/pixel in the annulus,\n"
            "      subtracted from the aperture sum (raw_sum - sky_median * N_pixels)\n"
            "  • Gain (e-/ADU)             — auto-filled from the FITS header's\n"
            "      EGAIN keyword if present ('Gain source' shows where it came\n"
            "      from); type over it manually at any time\n"
            "  • ADU scale                 — some capture pipelines left-shift a\n"
            "      sensor's native ADC reading into a wider container (e.g. a\n"
            "      12-bit ADC value stored as an exact multiple of 16 in 16-bit\n"
            "      pixels). Detected automatically on raw sensor files and divided\n"
            "      out before applying Gain, so Total electrons is correct even\n"
            "      though Raw/Sky-sub/Peak ADU show the literal stored values.\n"
            "      On calibrated/float files this pattern is destroyed by dark\n"
            "      subtraction and flat division, so detection is unreliable --\n"
            "      the field is left as-is instead of silently assuming no scale\n"
            "      is needed. If a calibrated file shows 'unverified', type in the\n"
            "      value you see on a raw frame shot with the same camera/gain;\n"
            "      otherwise Total electrons can be off by that same factor.\n"
            "  • Full well (e-)            — optional. Your camera's per-pixel\n"
            "      full-well capacity in electrons, if you know it. Not read from\n"
            "      the FITS header (that spec is essentially never present) and\n"
            "      not looked up from a camera database -- you supply it once per\n"
            "      camera/gain combination. When set, the Exposure Meter's\n"
            "      saturation check uses true electrons and works on calibrated/\n"
            "      float files too. When left blank, saturation falls back to the\n"
            "      ADC-ceiling proxy described below (raw sensor files only).\n"
            "  • Target electrons          — the total-aperture electron count\n"
            "      you're aiming for, default 100,000. This is the point past\n"
            "      which photon noise is usually no longer the dominant error\n"
            "      source in ground-based differential photometry (scintillation,\n"
            "      flat-fielding, guiding drift, etc. tend to dominate instead) --\n"
            "      not a hard rule, adjust it for a shallow transit, a bright\n"
            "      target, or a high-scintillation site.\n"
            "  • Total electrons           — (Sky-sub sum ÷ ADU scale) × Gain\n"
            "  • Exposure Meter            — replaces the old Level readout with\n"
            "      two bar gauges plus a text status/recommendation, all driven\n"
            "      by the same underlying check:\n"
            "        - Signal (total electrons) bar -- amber/green/blue zones for\n"
            "          under-target, on-target, and over-target, with a white\n"
            "          marker line at your current Total electrons and the raw\n"
            "          numbers labeled above the bar\n"
            "        - Saturation (peak) bar -- green/amber/red zones at 70% and\n"
            "          85% of full well (or the ADC-ceiling proxy), marker at\n"
            "          the current peak's position\n"
            "        - Status line             — one of the five states below,\n"
            "          color-coded green/yellow/blue/red\n"
            "        - Recommendation line     — plain 'increase/reduce/no\n"
            "          change' guidance, with a suggested new exposure time when\n"
            "          the FITS header has EXPTIME or EXPOSURE\n"
            "      The five status states, highest priority first:\n"
            "        - NEAR SATURATION (red) -- peak pixel is close to clipping\n"
            "          or going non-linear. Checked against Full well (e-) in\n"
            "          electrons if set, otherwise against the ADC ceiling (the\n"
            "          highest value the file's bit depth can represent, e.g.\n"
            "          65535 for 16-bit -- a proxy, not the camera's true full\n"
            "          well, and only meaningful on raw integer files).\n"
            "        - TOO FAINT (red) -- peak signal above sky is only a few\n"
            "          times the measured sky noise, computed directly from this\n"
            "          image's own sky annulus.\n"
            "        - LOW SIGNAL (yellow) -- Total electrons is well under\n"
            "          Target electrons; photon noise is higher than it needs to\n"
            "          be for a given amount of exposure time.\n"
            "        - EXCESS SIGNAL (blue) -- Total electrons is well over\n"
            "          Target electrons with no saturation risk; a shorter\n"
            "          exposure would likely trade unneeded photon-noise\n"
            "          precision for better cadence, without changing your\n"
            "          binned light-curve precision much.\n"
            "        - GOOD (green) -- none of the above triggered.\n"
            "  • Sequence playback         — the aperture stays fixed in place and\n"
            "      re-measures automatically on every frame/step of a sequence\n"
            "  • Clear Aperture button     — removes the aperture from the image\n"
            "  • Esc  (keyboard)           — same as Clear Aperture\n"
            "  • Closing the Photometry panel's X button also clears the aperture\n"
            "  • Opening a new file/dir    — also clears the aperture")

        txt.config(state="disabled")

        tk.Button(win, text="Close", command=win.destroy,
                  bg="#1a3060", fg="white", relief="flat",
                  font=("Segoe UI", 9), cursor="hand2", padx=12, pady=4
                  ).pack(pady=(0, 8))

    # --- Revision History ---
    def _show_revision_history(self):
        win = tk.Toplevel(self.root)
        win.title("Revision History")
        win.configure(bg="#1a1a2e")
        win.resizable(True, True)
        win.geometry("680x560")

        tf = tk.Frame(win, bg="#1a1a2e")
        tf.pack(fill="both", expand=True, padx=12, pady=12)

        txt = tk.Text(tf, bg="#1a1a2e", fg="#d8d8f0",
                      font=("Segoe UI", 10), relief="flat",
                      wrap="word", cursor="arrow", padx=10, pady=8)
        vsb = ttk.Scrollbar(tf, orient="vertical", command=txt.yview)
        txt.configure(yscrollcommand=vsb.set)
        vsb.pack(side="right", fill="y")
        txt.pack(fill="both", expand=True)

        txt.tag_configure("ver",    foreground=ACCENT, font=("Segoe UI", 11, "bold"))
        txt.tag_configure("bullet", foreground="#d8d8f0", font=("Segoe UI", 10))

        def entry(version, date, bullets):
            txt.insert("end", f"{version}  \u2014  {date}\n", "ver")
            for b in bullets:
                txt.insert("end", f"\u2022 {b}\n\n", "bullet")
            txt.insert("end", "\n")

        entry("v1.4.0", "2026-08-02", [
            "Fixed a real bug in Total electrons on calibrated/float FITS files: "
            "the ADU-scale auto-detector (which un-does a sensor's native ADC "
            "reading being left-shifted into a wider pixel container, e.g. 12-bit "
            "into 16-bit) relies on every pixel sharing an exact power-of-two "
            "divisor. Dark subtraction and flat division destroy that exact "
            "pattern, so detection silently failed on calibrated files and the "
            "app defaulted to 'no scale needed' -- overstating Total electrons by "
            "that same factor (e.g. 16x) on every calibrated frame. ADU scale is "
            "now an editable field: still auto-filled on raw sensor files, but "
            "left alone (not silently zeroed out) on calibrated files where "
            "detection is inconclusive, so a value carried over from a matching "
            "raw frame keeps working correctly.",
            "Added an optional Full well (e-) field. When set, it enables a true "
            "saturation check in electrons that works on calibrated/float files "
            "too, where the previous ADC-ceiling proxy (derived from BITPIX) "
            "doesn't apply. Left blank, saturation checking falls back to the "
            "ADC-ceiling proxy exactly as before.",
            "Added a Target electrons field (default 100,000) -- the point past "
            "which photon noise from the target star usually stops being the "
            "dominant error source in ground-based differential photometry, so "
            "collecting more just costs cadence without buying real precision.",
            "Replaced the Level (Low/Nominal/High) readout with an Exposure "
            "Meter: combines the saturation check, the existing too-faint/SNR "
            "check, and the new electron-target check into a color-coded status "
            "line, plus a plain-language recommendation (increase/reduce/no "
            "change) with a suggested new exposure time when the FITS header has "
            "EXPTIME or EXPOSURE.",
            "Added two bar gauges to the Exposure Meter so it's an actual visual "
            "meter, not just colored text: a Signal bar (amber/green/blue zones "
            "for under-target, on-target, and over-target Total electrons) and a "
            "Saturation bar (green/amber/red zones at the 70% and 85% peak "
            "thresholds), each with a marker showing the current reading and the "
            "raw numbers labeled above the bar.",
        ])
        entry("v1.3.0", "2026-07-12", [
            "Aperture photometry added. Click a star to auto-centroid on it and "
            "auto-size a circular aperture from its measured FWHM (radial "
            "half-max profile), or click-and-drag to size it manually. A "
            "sky-background annulus (cyan/magenta rings) is subtracted "
            "automatically using a sigma-clipped median. The Photometry panel "
            "(toolbar button or View menu) stays floating on top of the main "
            "window, auto-opens the first time you click to place an aperture, "
            "and reports pixel count, sky median, raw and sky-subtracted ADU "
            "sum, and peak ADU. Geometry spinboxes let you fine-tune the radius "
            "and annulus afterward either way.",
            "Total electron count is computed from the sky-subtracted ADU sum and a "
            "Gain (e-/ADU) value. Gain is auto-filled from the FITS header's EGAIN "
            "keyword when present, or can be typed in directly.",
            "Some capture pipelines store a sensor's native ADC reading left-shifted "
            "into a wider pixel container (e.g. a 12-bit value stored as an exact "
            "multiple of 16 in 16-bit pixels), which silently inflates a naive "
            "ADU-to-electron conversion. This is now auto-detected per image "
            "(bitwise scan for a common power-of-two divisor across every pixel) "
            "and divided out before applying Gain; the detected scale is shown "
            "in the Photometry panel.",
            "The aperture stays fixed in image coordinates across sequence frames "
            "and re-measures automatically on every frame step or during playback, "
            "for quick-look photometry across a light-curve sequence.",
            "The aperture can be removed at any time with the Clear Aperture "
            "button or the Esc key; it's also cleared automatically when opening "
            "a new file/directory or when the Photometry panel is closed.",
            "Fixed scroll-wheel zoom drifting away from the cursor position: "
            "the image previously moved up-left when zooming in and down-right "
            "when zooming out. Zoom now stays correctly anchored under the cursor.",
            "The window title bar now shows the open file's name. Default window "
            "size reduced to 1080×776 and the FITS Header window to "
            "336×650, both smaller and easier to fit on screen.",
            "The Photometry panel now shows a color-coded Level readout (Low / "
            "Nominal / High) for the current aperture: HIGH flags a peak pixel "
            "close to the sensor's ADC ceiling, LOW flags a peak signal only a "
            "few times the measured sky noise. Both are derived from BITPIX and "
            "the image's own data, not a saturation keyword (FITS headers "
            "rarely include one).",
        ])
        entry("v1.2.0", "2026-03-31", [
            "Black and White level slider thumbs now default to the centre of their "
            "range after each Auto Stretch, making trough clicks accessible on both "
            "sides. Trough-click position calculation updated to use the actual slider "
            "range rather than assuming a fixed 0\u20131 scale.",
            "All text in the histogram control panel increased by 50% for better "
            "readability.",
            "Histogram Zoom level now persists across sessions. The last-used zoom "
            "setting is saved to fits_viewer_settings.json and restored on next launch.",
        ])
        entry("v1.1.0", "2026-03-31", [
            "Help menu added to the menu bar. Instructions moved to Help \u2192 User "
            "Guide and enhanced with a clickable table of contents. "
            "Help \u2192 About dialog added with app name, version, and author "
            "information.",
            "Instructions button removed from the toolbar (now accessed via Help menu).",
        ])
        entry("v1.0.0", "2026-03-31", [
            "Initial release. Single FITS image and directory sequence viewer with "
            "autostretch, histogram with draggable level lines, zoom/pan, colormap "
            "selection, sequence playback, FITS header viewer/editor, and "
            "RA/Dec cursor readout for plate-solved images.",
        ])

        txt.config(state="disabled")

        tk.Button(win, text="Close", command=win.destroy,
                  bg="#1a3060", fg="white", relief="flat",
                  font=("Segoe UI", 9), cursor="hand2", padx=12, pady=4
                  ).pack(pady=(0, 10))

    # --- About ---
    def _show_about(self):
        win = tk.Toplevel(self.root)
        win.title("About Simple FITS Viewer")
        win.configure(bg=FRAME_COLOR)
        win.resizable(False, False)
        win.geometry("420x290")
        win.transient(self.root)
        win.grab_set()

        tk.Label(win, text="Simple FITS Viewer",
                 bg=FRAME_COLOR, fg="white",
                 font=("Segoe UI", 16, "bold")).pack(pady=(28, 2))

        tk.Label(win, text=f"Version {APP_VERSION}",
                 bg=FRAME_COLOR, fg=ACCENT,
                 font=("Segoe UI", 10)).pack()

        tk.Frame(win, bg=SEP_COLOR, height=1).pack(fill="x", padx=36, pady=16)

        tk.Label(win, text="Author",
                 bg=FRAME_COLOR, fg="white",
                 font=("Segoe UI", 10, "bold")).pack()

        tk.Label(win, text="Art Trail",
                 bg=FRAME_COLOR, fg=TEXT_COLOR,
                 font=("Segoe UI", 10)).pack()

        tk.Label(win, text="art.trail@icloud.com",
                 bg=FRAME_COLOR, fg=ACCENT,
                 font=("Segoe UI", 9, "italic")).pack()

        tk.Label(win, text="© 2026 Art Trail.  All rights reserved.",
                 bg=FRAME_COLOR, fg="#888899",
                 font=("Segoe UI", 9, "italic")).pack(pady=(4, 0))

        tk.Frame(win, bg=SEP_COLOR, height=1).pack(fill="x", padx=36, pady=16)

        tk.Button(win, text="Close", command=win.destroy,
                  bg="#1a3060", fg="white", relief="flat",
                  font=("Segoe UI", 9), cursor="hand2", padx=16, pady=4
                  ).pack(pady=(0, 20))

    # --- Status bar ---
    def _build_statusbar(self):
        sb = tk.Frame(self.root, bg="#0e0e1c", height=22)
        sb.pack(fill="x", side="bottom")
        sb.pack_propagate(False)

        lkw = dict(bg="#0e0e1c", fg="#6666aa", font=("Segoe UI", 8))
        self.sb_file  = tk.Label(sb, text="No file loaded", **lkw)
        self.sb_file.pack(side="left", padx=8)
        # Indeterminate progress bar, shown only while a background FITS load
        # is in flight (e.g. reading from a slow network share) -- not packed
        # by default, see _show_load_progress/_hide_load_progress.
        self.sb_progress = ttk.Progressbar(sb, mode="indeterminate", length=90)
        ttk.Separator(sb, orient="vertical").pack(side="left", fill="y", pady=2)
        self.sb_pixel = tk.Label(sb, text="", **lkw)
        self.sb_pixel.pack(side="left", padx=8)
        ttk.Separator(sb, orient="vertical").pack(side="left", fill="y", pady=2)
        self.sb_zoom  = tk.Label(sb, text="Zoom: 1.00×", **lkw)
        self.sb_zoom.pack(side="left", padx=8)
        self.sb_dims  = tk.Label(sb, text="", **lkw)
        self.sb_dims.pack(side="right", padx=8)
        tk.Label(sb, text="© Art Trail 2026", **lkw).pack(side="right", padx=12)

    def _show_load_progress(self, filename: str):
        self.sb_file.config(text=f"Loading {filename}…")
        if not self.sb_progress.winfo_ismapped():
            self.sb_progress.pack(side="left", padx=8, after=self.sb_file)
        self.sb_progress.start(12)

    def _hide_load_progress(self):
        self.sb_progress.stop()
        self.sb_progress.pack_forget()

    # --- Key bindings ---
    def _bind_keys(self):
        self.root.bind("<Control-o>", lambda e: self.open_file())
        self.root.bind("<Control-d>", lambda e: self.open_directory())
        self.root.bind("a",           lambda e: self.auto_stretch())
        self.root.bind("f",           lambda e: self.zoom_fit())
        self.root.bind("1",           lambda e: self.set_zoom(1.0))
        self.root.bind("+",           lambda e: self.zoom(1.25))
        self.root.bind("=",           lambda e: self.zoom(1.25))
        self.root.bind("-",           lambda e: self.zoom(0.8))
        self.root.bind("<Left>",      lambda e: self.frame_prev())
        self.root.bind("<Right>",     lambda e: self.frame_next())
        self.root.bind("<space>",     lambda e: self.toggle_play())
        self.root.bind("<Escape>",    lambda e: self._reset_aperture())

    # -----------------------------------------------------------------------
    # File Operations
    # -----------------------------------------------------------------------
    def open_file(self):
        path = filedialog.askopenfilename(
            title="Open FITS File",
            filetypes=[
                ("FITS files", "*.fits *.fit *.fts *.FITS *.FIT *.FTS"),
                ("All files", "*.*")
            ]
        )
        if not path:
            return
        self._reset_aperture()
        self.sequence_files = [path]
        self.current_frame  = 0
        self.load_fits(path, auto=True)
        self._update_playback_ui()

    def open_directory(self):
        directory = filedialog.askdirectory(title="Open Directory — FITS Sequence")
        if not directory:
            return
        exts = {".fits", ".fit", ".fts"}
        files = sorted(
            str(p) for p in Path(directory).iterdir()
            if p.suffix.lower() in exts
        )
        if not files:
            messagebox.showwarning("No FITS Files",
                                   f"No FITS files found in:\n{directory}")
            return
        self._reset_aperture()
        self.sequence_files = files
        self.current_frame  = 0
        self.load_fits(files[0], auto=True)
        self._update_playback_ui()

    def load_fits(self, path: str, auto: bool = True):
        """Load image data and header from a FITS file.

        The actual read runs on a background thread with a status-bar
        progress indicator, since a network-mounted file can take a few
        seconds and would otherwise freeze the window with no feedback (an
        indeterminate ttk.Progressbar only animates while the mainloop is
        free to process its timer events, which a blocking call on the main
        thread would prevent). _poll_load_fits (started once, in __init__)
        drains the result; a request id guards against a stale result
        winning a race against a newer one, e.g. rapid frame-stepping.
        """
        self._load_seq += 1
        req_id = self._load_seq
        self._show_load_progress(os.path.basename(path))
        threading.Thread(target=self._load_fits_worker, args=(req_id, path, auto), daemon=True).start()

    def _load_fits_worker(self, req_id: int, path: str, auto: bool):
        """Background thread: the actual (possibly slow, network-bound) file
        read. No Tkinter calls here -- only self._load_queue, which is safe
        to write from another thread; everything UI-facing happens in
        _apply_loaded_fits on the main thread instead.
        """
        try:
            with fits.open(path) as hdul:
                hdu = None
                for h in hdul:
                    if h.data is not None and h.data.ndim >= 2:
                        hdu = h
                        break
                if hdu is None:
                    self._load_queue.put((req_id, path, auto, "error", f"No image data in:\n{path}"))
                    return
                data = hdu.data.astype(np.float64)
                header = hdu.header.copy()
            self._load_queue.put((req_id, path, auto, "ok", (data, header)))
        except Exception as exc:
            self._load_queue.put((req_id, path, auto, "error", str(exc)))

    def _poll_load_fits(self):
        """Persistent drain loop for _load_queue, started once in __init__
        and self-rescheduling forever. A single loop (rather than one per
        dispatched load) avoids two concurrent pollers racing to dequeue
        each other's result from the shared queue.
        """
        try:
            while True:
                req_id, path, auto, status, payload = self._load_queue.get_nowait()
                if req_id != self._load_seq:
                    continue   # superseded by a newer load request -- discard
                self._hide_load_progress()
                if status == "error":
                    self.sb_file.config(
                        text=os.path.basename(self.fits_file) if self.fits_file else "No file loaded")
                    messagebox.showerror("Error Loading FITS", str(payload))
                else:
                    data, header = payload
                    self._apply_loaded_fits(path, data, header, auto)
        except queue.Empty:
            pass
        self.root.after(30, self._poll_load_fits)

    def _apply_loaded_fits(self, path: str, data: np.ndarray, header, auto: bool):
        """Everything that happens once a FITS file's data/header are in
        hand -- runs on the main thread, via _poll_load_fits, after the
        background read completes.
        """
        try:
            self.fits_header = header

            # Build WCS if the header contains plate-solve data
            try:
                wcs = WCS(self.fits_header, naxis=2)
                self._wcs = wcs if wcs.has_celestial else None
            except Exception:
                self._wcs = None

            # Collapse 3-D cube to 2-D (take first plane)
            if data.ndim == 3:
                data = data[0]

            self.fits_data = data
            self.fits_file = path

            # ADU scale (left-shift divisor, e.g. a 12-bit ADC packed into 16-bit
            # storage). The bitwise-OR detector is exact for integer sensor data,
            # but calibration (dark subtraction, flat division) introduces
            # fractional values that destroy the exact-multiple pattern -- so on
            # calibrated/float files a "nothing detected" result does NOT mean no
            # scaling is needed, it means the check is inconclusive. Overwriting
            # the field with 1 in that case would silently reintroduce the same
            # under/over-count bug that flat-out mis-stated an ~16x inflated
            # electron count. So: only auto-overwrite when either a real pattern
            # is found, or the data is integer (where "1" is a trustworthy result,
            # not a guess) -- otherwise leave whatever the field already holds
            # (typically carried over from a raw frame shot with the same setup).
            detected = self._detect_adu_divisor(data)
            bitpix = self.fits_header.get("BITPIX")
            is_integer_data = bitpix is not None and bitpix > 0
            if detected > 1 or is_integer_data:
                self._setting_scale_programmatically = True
                self.adu_scale.set(str(detected))
                self._setting_scale_programmatically = False
                self._adu_scale_source = "auto-detected"
            elif not self.adu_scale.get().strip():
                self._adu_scale_source = "unverified (calibrated -- check a raw frame)"
            # else: float/calibrated data, nothing detected, field already has a
            # value (persisted or user-typed) -- leave it alone.

            egain = self.fits_header.get("EGAIN")
            if egain is not None:
                try:
                    egain_val = float(egain)
                    self._setting_gain_programmatically = True
                    self.gain_eperadu.set(f"{egain_val:.6g}")
                    self._setting_gain_programmatically = False
                    self._gain_source = "FITS header (EGAIN)"
                except (TypeError, ValueError):
                    pass

            # Compute data range once
            valid = data[np.isfinite(data)]
            if len(valid) == 0:
                valid = data.ravel()
            self._data_range = (float(valid.min()), float(valid.max()))

            if auto:
                self.auto_stretch()
                self._redraw_histogram()
                self.root.after(0, self.zoom_fit)
            else:
                self.apply_stretch()
                if not self.playing:
                    self._redraw_histogram()

            if self.header_visible.get():
                self.populate_header()

            self.sb_file.config(text=os.path.basename(path))
            self._update_title(os.path.basename(path))
            h, w = data.shape
            self.sb_dims.config(text=f"{w} × {h} px")

            # Aperture position is kept across sequence frames (same field of
            # view) so photometry re-measures automatically frame to frame.
            if self.aperture_center is not None:
                self._compute_photometry()

        except Exception as exc:
            messagebox.showerror("Error Loading FITS", str(exc))

    # -----------------------------------------------------------------------
    # Stretch / Display
    # -----------------------------------------------------------------------
    def auto_stretch(self):
        """Compute optimal black/white levels and redisplay."""
        if self.fits_data is None:
            return

        data = self.fits_data
        mode = self.stretch_mode.get()
        valid = data[np.isfinite(data)]
        if len(valid) == 0:
            return

        try:
            if mode == "ZScale":
                vmin, vmax = ZScaleInterval().get_limits(data)
            elif mode == "Percentile 99.5%":
                vmin, vmax = PercentileInterval(99.5).get_limits(data)
            elif mode == "Percentile 98%":
                vmin, vmax = PercentileInterval(98.0).get_limits(data)
            elif mode == "MinMax":
                vmin, vmax = float(valid.min()), float(valid.max())
            else:  # Linear 5-99%
                vmin = float(np.percentile(valid, 5))
                vmax = float(np.percentile(valid, 99))
        except Exception:
            vmin, vmax = float(valid.min()), float(valid.max())

        dmin, dmax = self._data_range
        drange = max(dmax - dmin, 1e-10)
        bl = max(0.0, min(1.0, (vmin - dmin) / drange))
        wl = max(0.0, min(1.0, (vmax - dmin) / drange))
        if wl <= bl:
            wl = min(1.0, bl + 0.01)

        self.black_level.set(bl)
        self.white_level.set(wl)
        self._center_sliders(bl, wl)
        self.apply_stretch()

    def apply_stretch(self):
        """Map raw data through current levels and colormap → display image."""
        if self.fits_data is None:
            return

        dmin, dmax = self._data_range
        drange = max(dmax - dmin, 1e-10)
        bl = self.black_level.get()
        wl = self.white_level.get()
        if wl <= bl:
            wl = bl + 1e-6

        vmin = dmin + bl * drange
        vmax = dmin + wl * drange

        norm = (self.fits_data - vmin) / (vmax - vmin)
        norm = np.clip(norm, 0.0, 1.0)

        rgb = self._apply_colormap(norm, self.colormap.get())
        self.display_image = Image.fromarray(rgb, mode="RGB")

        self._redraw_canvas()
        self._update_histogram_levels()

    def _apply_colormap(self, norm: np.ndarray, name: str) -> np.ndarray:
        """Fast LUT-based colormap — builds a 256-entry table once, then indexes."""
        if name not in self._cmap_lut_cache:
            cmap = _get_cmap(name)
            self._cmap_lut_cache[name] = (
                cmap(np.linspace(0, 1, 256))[:, :3] * 255
            ).astype(np.uint8)
        lut = self._cmap_lut_cache[name]
        indices = (norm * 255.0).clip(0, 255).astype(np.uint8)
        return lut[indices]

    # -----------------------------------------------------------------------
    # Canvas drawing
    # -----------------------------------------------------------------------
    def _redraw_canvas(self):
        if self.display_image is None:
            return

        self.canvas.delete("placeholder")
        self.canvas.delete("fits_img")

        iw, ih = self.display_image.size
        nw = max(1, int(iw * self.zoom_level))
        nh = max(1, int(ih * self.zoom_level))

        if self.zoom_level >= 1.0 or self.playing:
            resample = Image.NEAREST
        else:
            resample = Image.BILINEAR
        scaled = self.display_image.resize((nw, nh), resample)
        self.tk_image = ImageTk.PhotoImage(scaled)

        cw = max(self.canvas.winfo_width(),  1)
        ch = max(self.canvas.winfo_height(), 1)

        # Centre image when smaller than canvas
        x0 = max(0, (cw - nw) // 2) + self.pan_x
        y0 = max(0, (ch - nh) // 2) + self.pan_y

        sr_w = max(nw + max(0, self.pan_x), cw)
        sr_h = max(nh + max(0, self.pan_y), ch)
        self.canvas.configure(scrollregion=(0, 0, sr_w, sr_h))
        self.canvas.create_image(x0, y0, anchor="nw", image=self.tk_image, tags="fits_img")

        self.sb_zoom.config(text=f"Zoom: {self.zoom_level:.2f}×")

        if self.aperture_center is not None:
            self._draw_aperture_overlay()

    # -----------------------------------------------------------------------
    # Histogram
    # -----------------------------------------------------------------------
    def _redraw_histogram(self):
        if self.fits_data is None:
            return

        ax = self.hist_ax
        ax.clear()
        ax.set_facecolor("#12121e")

        dmin, dmax = self._data_range
        valid = self.fits_data[np.isfinite(self.fits_data)].ravel()

        # Zoom X axis to where data actually lives (clip_pct%–(100-clip_pct)% percentile)
        clip = self._hist_clip_pct
        if clip > 0:
            xlo = float(np.percentile(valid, clip))
            xhi = float(np.percentile(valid, 100 - clip))
        else:
            xlo, xhi = float(valid.min()), float(valid.max())
        margin = (xhi - xlo) * 0.05 or 1.0
        xlo -= margin
        xhi += margin

        ax.hist(valid, bins=512, range=(xlo, xhi),
                color=ACCENT, alpha=0.75, linewidth=0)

        bl = self.black_level.get()
        wl = self.white_level.get()
        vmin = dmin + bl * (dmax - dmin)
        vmax = dmin + wl * (dmax - dmin)

        self._black_vline = ax.axvline(vmin, color="#ff4444", linewidth=1.5,
                                        linestyle="--", label="Black")
        self._white_vline = ax.axvline(vmax, color="#ffcc55", linewidth=1.5,
                                        linestyle="--", label="White")

        # Always ensure level lines are visible
        xlo = min(xlo, vmin - margin)
        xhi = max(xhi, vmax + margin)

        ax.set_xlim(xlo, xhi)
        ax.tick_params(labelsize=6, colors="#6666aa", length=2)
        for sp in ax.spines.values():
            sp.set_color(SEP_COLOR)
        ax.set_title("Histogram  (drag red/yellow lines or use sliders)",
                     color="#6666aa", fontsize=6, pad=2)

        self.hist_fig.patch.set_facecolor("#12121e")
        self.hist_canvas_widget.draw_idle()

    def _update_histogram_levels(self):
        """Fast path: just reposition the two vlines, no histogram rebuild."""
        if self._black_vline is None or self._white_vline is None:
            self._redraw_histogram()
            return
        dmin, dmax = self._data_range
        bl = self.black_level.get()
        wl = self.white_level.get()
        self._black_vline.set_xdata([dmin + bl * (dmax - dmin)] * 2)
        self._white_vline.set_xdata([dmin + wl * (dmax - dmin)] * 2)
        self.hist_canvas_widget.draw_idle()

    # Histogram zoom steps: 0 = full range, finer steps near the bottom
    _HIST_ZOOM_STEPS = [0, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 20.0]

    def _hist_zoom_in(self):
        """Increase clip percentile → narrower (more zoomed-in) histogram view."""
        steps = self._HIST_ZOOM_STEPS
        cur = self._hist_clip_pct
        candidates = [s for s in steps if s > cur + 1e-9]
        self._hist_clip_pct = candidates[0] if candidates else steps[-1]
        self._save_hist_zoom()
        self._update_hist_zoom_label()
        self._redraw_histogram()

    def _hist_zoom_out(self):
        """Decrease clip percentile → wider (more zoomed-out) histogram view."""
        steps = self._HIST_ZOOM_STEPS
        cur = self._hist_clip_pct
        candidates = [s for s in steps if s < cur - 1e-9]
        self._hist_clip_pct = candidates[-1] if candidates else steps[0]
        self._save_hist_zoom()
        self._update_hist_zoom_label()
        self._redraw_histogram()

    def _load_hist_zoom(self) -> float:
        """Load the last-used histogram zoom percentile from settings; default 0.5."""
        try:
            with open(_SETTINGS_PATH, "r") as f:
                return float(json.load(f).get("hist_clip_pct", 0.5))
        except Exception:
            return 0.5

    def _save_hist_zoom(self):
        """Persist the current histogram zoom percentile to settings."""
        try:
            data = {}
            if _SETTINGS_PATH.exists():
                with open(_SETTINGS_PATH, "r") as f:
                    data = json.load(f)
            data["hist_clip_pct"] = self._hist_clip_pct
            with open(_SETTINGS_PATH, "w") as f:
                json.dump(data, f)
        except Exception:
            pass

    def _update_hist_zoom_label(self):
        clip = self._hist_clip_pct
        if clip == 0:
            txt = "Full"
        else:
            txt = f"{clip:g}%"
        self._hist_zoom_lbl.config(text=txt)

    def _center_sliders(self, bl: float, wl: float):
        """Reconfigure slider ranges so each thumb sits near the centre."""
        res = 0.001
        # Black slider: from_=0, to=2*bl so bl is at ~50%
        blk_to = min(1.0 - res, max(2.0 * bl, wl + res))
        self.black_slider.config(from_=0.0, to=blk_to, resolution=res)

        # White slider: symmetric half-range around wl, clamped to [bl+res, 1.0]
        half = max(wl - bl - res, 0.01)
        wht_from = max(bl + res, wl - half)
        wht_to   = min(1.0,     wl + half)
        self.white_slider.config(from_=wht_from, to=wht_to, resolution=res)

    def _bind_slider_trough(self, slider: tk.Scale, which: str):
        """Intercept trough clicks so they step by 5 (0.005) instead of jumping."""
        def on_press(event):
            slider.update_idletasks()
            w = slider.winfo_width()
            if w < 2:
                return
            # Compute thumb position using actual slider range (not assumed 0–1)
            from_  = float(slider.cget("from"))
            to_    = float(slider.cget("to"))
            span   = to_ - from_ if to_ != from_ else 1.0
            frac   = (slider.get() - from_) / span
            thumb_x   = frac * w
            thumb_half = max(8, w // 16)   # ≈ half the thumb width in pixels
            if event.x < thumb_x - thumb_half:
                self._bump_level(which, -0.005)
                return "break"
            elif event.x > thumb_x + thumb_half:
                self._bump_level(which, +0.005)
                return "break"
            # Click landed on the thumb itself — let the Scale handle drag normally
        slider.bind("<Button-1>", on_press)

    def _bump_level(self, which: str, delta: float):
        var = self.black_level if which == "black" else self.white_level
        var.set(max(0.0, min(1.0, var.get() + delta)))
        bl = self.black_level.get()
        wl = self.white_level.get()
        if wl <= bl:
            self.white_level.set(min(1.0, bl + 0.001))
        self.apply_stretch()

    def _on_level_changed(self):
        """Called by slider drag — debounced so rapid moves coalesce."""
        if self._level_after_id is not None:
            self.root.after_cancel(self._level_after_id)
        self._level_after_id = self.root.after(40, self._apply_level_change)

    def _apply_level_change(self):
        self._level_after_id = None
        bl = self.black_level.get()
        wl = self.white_level.get()
        if wl <= bl:
            self.white_level.set(min(1.0, bl + 0.001))
        self.apply_stretch()

    # Histogram drag
    def _hist_press(self, event):
        if event.inaxes is not self.hist_ax or self.fits_data is None:
            return
        if event.xdata is None:
            return
        dmin, dmax = self._data_range
        drange = max(dmax - dmin, 1e-10)
        bl = self.black_level.get()
        wl = self.white_level.get()
        vmin = dmin + bl * drange
        vmax = dmin + wl * drange
        tol  = drange * 0.025
        if abs(event.xdata - vmin) < tol:
            self._hist_dragging = "black"
        elif abs(event.xdata - vmax) < tol:
            self._hist_dragging = "white"

    def _hist_motion(self, event):
        if self._hist_dragging is None or event.inaxes is not self.hist_ax:
            return
        if event.xdata is None:
            return
        dmin, dmax = self._data_range
        drange = max(dmax - dmin, 1e-10)
        val = max(0.0, min(1.0, (event.xdata - dmin) / drange))

        if self._hist_dragging == "black":
            if val < self.white_level.get():
                self.black_level.set(val)
                self.black_slider.set(val)
        else:
            if val > self.black_level.get():
                self.white_level.set(val)
                self.white_slider.set(val)
        self.apply_stretch()

    def _hist_release(self, event):
        self._hist_dragging = None

    # -----------------------------------------------------------------------
    # Zoom / Pan
    # -----------------------------------------------------------------------
    def zoom(self, factor: float):
        self.zoom_level = max(0.02, min(64.0, self.zoom_level * factor))
        self._redraw_canvas()

    def set_zoom(self, level: float):
        self.zoom_level = level
        self.pan_x = 0
        self.pan_y = 0
        self._redraw_canvas()

    def zoom_fit(self):
        if self.display_image is None:
            return
        cw = max(self.canvas.winfo_width(),  1)
        ch = max(self.canvas.winfo_height(), 1)
        iw, ih = self.display_image.size
        self.zoom_level = min(cw / iw, ch / ih) * 0.97
        self.pan_x = 0
        self.pan_y = 0
        self._redraw_canvas()

    def reset_pan(self):
        self.pan_x = 0
        self.pan_y = 0
        self._redraw_canvas()

    def _on_mousewheel(self, event):
        if self.display_image is None:
            return
        factor = 1.1 if event.delta > 0 else 0.9
        cx = self.canvas.canvasx(event.x)
        cy = self.canvas.canvasy(event.y)

        # Image pixel currently under the cursor, using the pre-zoom transform
        px, py = self._canvas_to_img(cx, cy)

        old = self.zoom_level
        self.zoom_level = max(0.02, min(64.0, self.zoom_level * factor))
        if self.zoom_level == old:
            return

        # _redraw_canvas re-centers the image whenever it's smaller than the
        # canvas (max(0, (cw-nw)//2)), so pan can't be scaled by a simple
        # ratio — solve it directly so (px, py) lands back under the cursor.
        cw = max(self.canvas.winfo_width(),  1)
        ch = max(self.canvas.winfo_height(), 1)
        iw, ih = self.display_image.size
        nw = int(iw * self.zoom_level)
        nh = int(ih * self.zoom_level)
        offset_x = max(0, (cw - nw) // 2)
        offset_y = max(0, (ch - nh) // 2)
        self.pan_x = int(cx - px * self.zoom_level - offset_x)
        self.pan_y = int(cy - py * self.zoom_level - offset_y)
        self._redraw_canvas()

    def _pan_press(self, event):
        self._pan_start_pos    = (event.x, event.y)
        self._pan_start_offset = (self.pan_x, self.pan_y)

    def _pan_drag(self, event):
        if self._pan_start_pos is None:
            return
        dx = event.x - self._pan_start_pos[0]
        dy = event.y - self._pan_start_pos[1]
        self.pan_x = self._pan_start_offset[0] + dx
        self.pan_y = self._pan_start_offset[1] + dy
        self._redraw_canvas()

    def _img_to_canvas_origin(self):
        """Canvas (x0, y0) where displayed image pixel (0, 0) currently sits."""
        cw = max(self.canvas.winfo_width(),  1)
        ch = max(self.canvas.winfo_height(), 1)
        iw, ih = self.display_image.size
        x0 = max(0, (cw - int(iw * self.zoom_level)) // 2) + self.pan_x
        y0 = max(0, (ch - int(ih * self.zoom_level)) // 2) + self.pan_y
        return x0, y0

    def _canvas_to_img(self, cx, cy):
        """Canvas coords -> float image-pixel coords."""
        x0, y0 = self._img_to_canvas_origin()
        return (cx - x0) / self.zoom_level, (cy - y0) / self.zoom_level

    def _img_to_canvas(self, px, py):
        """Float image-pixel coords -> canvas coords."""
        x0, y0 = self._img_to_canvas_origin()
        return x0 + px * self.zoom_level, y0 + py * self.zoom_level

    def _on_canvas_motion(self, event):
        if self.fits_data is None or self.display_image is None:
            return
        cx = self.canvas.canvasx(event.x)
        cy = self.canvas.canvasy(event.y)
        px_f, py_f = self._canvas_to_img(cx, cy)
        px, py = int(px_f), int(py_f)
        h, w = self.fits_data.shape
        if 0 <= px < w and 0 <= py < h:
            val = self.fits_data[py, px]
            txt = f"X: {px}   Y: {py}   ADU: {val:.6g}"
            if self._wcs is not None:
                try:
                    sky = self._wcs.pixel_to_world(px, py)
                    ra  = sky.ra.to_string(unit="hour", sep=":", precision=2, pad=True)
                    dec = sky.dec.to_string(sep=":", precision=1, alwayssign=True, pad=True)
                    txt += f"   RA: {ra}   Dec: {dec}"
                except Exception:
                    pass
            self.tb_pixel.config(text=txt)
            self.sb_pixel.config(text="")
        else:
            self.tb_pixel.config(text="")
            self.sb_pixel.config(text="")

    def _on_canvas_resize(self, event):
        if self._resize_after_id is not None:
            self.root.after_cancel(self._resize_after_id)
        self._resize_after_id = self.root.after(80, self._redraw_canvas)

    # -----------------------------------------------------------------------
    # Aperture Photometry
    # -----------------------------------------------------------------------
    def _on_gain_var_written(self, *_):
        """Track whether the gain value came from the user typing it directly."""
        if not self._setting_gain_programmatically:
            self._gain_source = ""

    def _on_scale_var_written(self, *_):
        """Track whether the ADU scale value came from the user typing it directly."""
        if not self._setting_scale_programmatically:
            self._adu_scale_source = "manual entry"

    def _reset_aperture(self):
        """Clear the aperture — called when a genuinely new file/sequence is opened."""
        self.aperture_center = None
        self._phot_press_pos = None
        self.canvas.delete("phot_overlay")
        self._update_photometry_results(None)

    def _phot_press(self, event):
        if self.fits_data is None:
            return
        cx = self.canvas.canvasx(event.x)
        cy = self.canvas.canvasy(event.y)
        px, py = self._canvas_to_img(cx, cy)
        h, w = self.fits_data.shape
        if not (0 <= px < w and 0 <= py < h):
            return
        self._phot_press_pos = (px, py)
        self._phot_dragged = False
        self.aperture_center = (px, py)
        if not self.phot_visible.get():
            self._toolbar_toggle_photometry()
        self._draw_aperture_overlay()

    def _phot_motion(self, event):
        if self._phot_press_pos is None:
            return
        cx = self.canvas.canvasx(event.x)
        cy = self.canvas.canvasy(event.y)
        px, py = self._canvas_to_img(cx, cy)
        cx0, cy0 = self._phot_press_pos
        r = math.hypot(px - cx0, py - cy0)
        if r >= 2.0:
            self.aperture_radius.set(r)
            self._phot_dragged = True
        self._draw_aperture_overlay()

    def _phot_release(self, event):
        if self._phot_press_pos is None:
            return
        if not self._phot_dragged:
            # A plain click (no drag) -- auto-size around whatever star is there
            self._auto_size_aperture(*self._phot_press_pos)
            self._draw_aperture_overlay()
        self._phot_press_pos = None
        self._compute_photometry()
        self._save_phot_settings()

    def _auto_size_aperture(self, cx: float, cy: float):
        """Estimate a star's FWHM around (cx, cy) from its radial profile and
        size the aperture/annulus to match. If no clear point source is found
        near the click (e.g. blank sky), the existing radius/annulus is left
        untouched -- only the center (already set by _phot_press) applies.
        """
        if self.fits_data is None:
            return
        h, w = self.fits_data.shape
        box = 30
        x0 = max(0, int(cx) - box); x1 = min(w, int(cx) + box + 1)
        y0 = max(0, int(cy) - box); y1 = min(h, int(cy) + box + 1)
        if x1 <= x0 or y1 <= y0:
            return

        sub = self.fits_data[y0:y1, x0:x1]
        valid = np.isfinite(sub)
        if not valid.any():
            return

        bg_median, bg_sigma = self._sigma_clipped_median(sub[valid])
        bg_sigma = max(bg_sigma, 1e-6)

        # Find the peak in a SMALL window around the click, not the whole box.
        # Searching the full box would let a brighter neighbor several pixels
        # away steal the peak from a fainter star right under the cursor --
        # e.g. clicking the fainter member of a close double jumping to its
        # brighter companion.
        peak_search = 6
        py_c, px_c = int(cy) - y0, int(cx) - x0   # click position in sub-array coords
        sy0, sy1 = max(0, py_c - peak_search), min(sub.shape[0], py_c + peak_search + 1)
        sx0, sx1 = max(0, px_c - peak_search), min(sub.shape[1], px_c + peak_search + 1)
        local = np.where(valid[sy0:sy1, sx0:sx1], sub[sy0:sy1, sx0:sx1], -np.inf)
        py_l, px_l = np.unravel_index(np.argmax(local), local.shape)
        py_i, px_i = py_l + sy0, px_l + sx0
        peak_val = float(sub[py_i, px_i])

        # Significance test: the mean of a small 3x3 patch around the peak,
        # not just the single brightest pixel. A single noise pixel can spike
        # several sigma above background just from picking the max of ~3600
        # samples in this search box (order-statistics), but real stars have
        # spatially correlated signal across neighboring pixels -- background
        # noise doesn't. This also rejects lone cosmic-ray hits.
        py0, py1 = max(0, py_i - 1), min(sub.shape[0], py_i + 2)
        px0, px1 = max(0, px_i - 1), min(sub.shape[1], px_i + 2)
        patch = sub[py0:py1, px0:px1]
        patch_mean = float(patch.mean())
        patch_significance = (patch_mean - bg_median) / (bg_sigma / math.sqrt(patch.size))

        if patch_significance < 8.0:
            return  # nothing star-like here -- keep the current geometry

        # Flux-weighted centroid in a small window around the peak (sub-pixel)
        win = 6
        wy0, wy1 = max(0, py_i - win), min(sub.shape[0], py_i + win + 1)
        wx0, wx1 = max(0, px_i - win), min(sub.shape[1], px_i + win + 1)
        yy, xx = np.mgrid[wy0:wy1, wx0:wx1]
        weights = np.clip(sub[wy0:wy1, wx0:wx1] - bg_median, 0, None)
        wsum = float(weights.sum())
        if wsum > 0:
            cen_x = float((xx * weights).sum() / wsum) + x0
            cen_y = float((yy * weights).sum() / wsum) + y0
        else:
            cen_x, cen_y = px_i + x0, py_i + y0

        # Radial profile from the centroid outward -- find the half-max radius
        yy, xx = np.mgrid[y0:y1, x0:x1]
        dist = np.sqrt((xx - cen_x) ** 2 + (yy - cen_y) ** 2)
        half_level = bg_median + 0.5 * (peak_val - bg_median)

        max_r = float(min(box, dist.max()))
        half_r = None
        r = 1.0
        while r <= max_r:
            ring = valid & (dist >= r - 0.5) & (dist < r + 0.5)
            if ring.any() and float(np.median(sub[ring])) <= half_level:
                half_r = r
                break
            r += 1.0
        if half_r is None:
            half_r = max_r * 0.5   # star wider than the search box -- best guess

        fwhm   = 2.0 * half_r
        radius = max(3.0, round(1.5 * fwhm, 1))   # generous margin past FWHM

        self.aperture_center = (cen_x, cen_y)
        self.aperture_radius.set(radius)
        self.annulus_inner.set(radius + 6.0)
        self.annulus_outer.set(radius + 16.0)

    def _draw_aperture_overlay(self):
        self.canvas.delete("phot_overlay")
        if self.aperture_center is None:
            return
        cx, cy = self.aperture_center
        r      = self.aperture_radius.get()
        r_in   = self.annulus_inner.get()
        r_out  = self.annulus_outer.get()

        def circle(radius, color, dash=None):
            x0, y0 = self._img_to_canvas(cx - radius, cy - radius)
            x1, y1 = self._img_to_canvas(cx + radius, cy + radius)
            kw = dict(outline=color, width=2, tags="phot_overlay")
            if dash:
                kw["dash"] = dash
            self.canvas.create_oval(x0, y0, x1, y1, **kw)

        circle(r,     "#33ff66")
        circle(r_in,  "#33ccff", dash=(4, 3))
        circle(r_out, "#ff44cc", dash=(4, 3))

        mx, my = self._img_to_canvas(cx, cy)
        self.canvas.create_line(mx - 6, my, mx + 6, my, fill="#33ff66", tags="phot_overlay")
        self.canvas.create_line(mx, my - 6, mx, my + 6, fill="#33ff66", tags="phot_overlay")

    @staticmethod
    def _detect_adu_divisor(data: np.ndarray, max_shift_bits: int = 8) -> int:
        """Detect a common power-of-two divisor shared by every pixel value.

        Some capture pipelines left-shift a sensor's native ADC reading (e.g.
        12-bit) into a wider container (e.g. 16-bit) rather than storing it in
        the low bits, so every stored value ends up an exact multiple of a
        power of two (16x for a 12-into-16-bit shift). Detected via a
        bitwise-OR reduction: any bit that's 0 in the OR of every pixel value
        is 0 in *every* pixel, i.e. all values share that as a common divisor.
        """
        valid = data[np.isfinite(data)]
        if valid.size == 0:
            return 1
        try:
            or_all = int(np.bitwise_or.reduce(valid.astype(np.int64)))
        except Exception:
            return 1
        if or_all == 0:
            return 1
        shift = 0
        while shift < max_shift_bits and (or_all & 1) == 0:
            shift += 1
            or_all >>= 1
        return 1 << shift

    @staticmethod
    def _sigma_clipped_median(arr: np.ndarray, sigma: float = 3.0, iters: int = 3):
        """Iteratively sigma-clip an array, returning (median, stddev) of what remains."""
        arr = arr.astype(np.float64)
        for _ in range(iters):
            if arr.size == 0:
                break
            med = np.median(arr)
            std = np.std(arr)
            if std == 0:
                break
            keep = np.abs(arr - med) <= sigma * std
            if keep.sum() == arr.size:
                break
            arr = arr[keep]
        if arr.size == 0:
            return 0.0, 0.0
        return float(np.median(arr)), float(np.std(arr))

    def _compute_photometry(self):
        if self.fits_data is None or self.aperture_center is None:
            return

        cx, cy = self.aperture_center
        r      = self.aperture_radius.get()
        r_in   = self.annulus_inner.get()
        r_out  = self.annulus_outer.get()

        # Keep annulus geometry sane relative to the aperture radius
        if r_in <= r:
            r_in = r + 2.0
            self.annulus_inner.set(r_in)
        if r_out <= r_in:
            r_out = r_in + 8.0
            self.annulus_outer.set(r_out)

        h, w = self.fits_data.shape
        x0 = max(0, int(math.floor(cx - r_out - 1)))
        x1 = min(w, int(math.ceil(cx + r_out + 1)))
        y0 = max(0, int(math.floor(cy - r_out - 1)))
        y1 = min(h, int(math.ceil(cy + r_out + 1)))
        if x1 <= x0 or y1 <= y0:
            return

        sub = self.fits_data[y0:y1, x0:x1]
        yy, xx = np.mgrid[y0:y1, x0:x1]
        dist2 = (xx - cx) ** 2 + (yy - cy) ** 2

        valid   = np.isfinite(sub)
        ap_mask = (dist2 <= r * r) & valid
        an_mask = (dist2 >= r_in * r_in) & (dist2 <= r_out * r_out) & valid

        n_ap = int(ap_mask.sum())
        if n_ap == 0:
            self._update_photometry_results(None)
            return

        ap_pixels = sub[ap_mask]
        raw_sum   = float(ap_pixels.sum())
        peak      = float(ap_pixels.max())

        an_pixels = sub[an_mask]
        sky_median, sky_sigma = self._sigma_clipped_median(an_pixels)

        sky_total = sky_median * n_ap
        sub_sum   = raw_sum - sky_total

        scale_str = self.adu_scale.get().strip()
        try:
            divisor = float(scale_str) if scale_str else 1.0
            if divisor <= 0:
                divisor = 1.0
        except ValueError:
            divisor = 1.0

        electrons = None
        gain_str  = self.gain_eperadu.get().strip()
        if gain_str:
            try:
                electrons = (sub_sum / divisor) * float(gain_str)
            except ValueError:
                electrons = None

        peak_electrons = None
        if gain_str:
            try:
                peak_electrons = (peak / divisor) * float(gain_str)
            except ValueError:
                peak_electrons = None

        meter = self._evaluate_exposure_meter(peak, peak_electrons, sky_median, sky_sigma, electrons)

        self._update_photometry_results(dict(
            cx=cx, cy=cy, r=r, r_in=r_in, r_out=r_out, n_ap=n_ap,
            sky_median=sky_median, sky_sigma=sky_sigma,
            raw_sum=raw_sum, sub_sum=sub_sum, peak=peak,
            electrons=electrons, divisor=divisor,
            gain_source=self._gain_source, adu_scale_source=self._adu_scale_source,
            meter=meter,
        ))

    # Exposure Meter thresholds. Saturation takes priority over the SNR floor
    # if somehow both trip (shouldn't normally happen -- a saturated peak
    # swamps sky noise).
    _SATURATION_WARN_PCT = 85.0   # % of full well / ADC ceiling considered "near saturation"
    _LOW_SNR_THRESHOLD   = 10.0   # peak-above-sky / sky-sigma considered "too faint"

    def _saturation_ceiling(self):
        """Max representable ADU, in the same stored/raw units as `peak` in
        _compute_photometry (i.e. the literal values in self.fits_data) --
        derived from BITPIX alone, not from any header field like
        SATURATE/FULLWELL, which capture software rarely writes. No ADU-scale
        divisor adjustment needed here: an auto-detected left-shift (e.g. a
        12-bit ADC packed into 16-bit storage) still saturates at ~2^BITPIX-1
        in stored units, just like an unshifted 16-bit reading would.
        Returns None for float-typed data (BITPIX <= 0), where a bit-depth
        ceiling isn't meaningful (e.g. calibrated/stacked/normalized images).
        """
        if self.fits_header is None:
            return None
        bitpix = self.fits_header.get("BITPIX")
        if bitpix is None or bitpix <= 0:
            return None
        return float(2 ** int(bitpix) - 1)

    # Electron-target band: below LOW_FRAC of the target, exposure is judged too
    # short for a good photon-noise floor; above HIGH_MULT of the target, photon
    # noise is almost certainly already well under typical ground-based
    # systematics (scintillation, flat-fielding, guiding) so more signal just
    # costs cadence for no real precision gain.
    _TARGET_LOW_FRAC  = 0.7
    _TARGET_HIGH_MULT = 3.0

    # Bar-gauge geometry
    _METER_PAD   = 4
    _METER_BAR_W = 332
    _METER_BAR_H = 14

    def _draw_exposure_meter(self, meter: dict | None):
        """Render the two bar gauges (signal vs. target, peak vs. saturation)
        onto the Exposure Meter canvas -- a real visual readout, not just the
        color-coded status text above it.
        """
        c = self._phot_meter_canvas
        if c is None:
            return
        c.delete("all")
        if not meter:
            return

        x0 = self._METER_PAD
        w  = self._METER_BAR_W
        h  = self._METER_BAR_H

        def draw_bar(y, zones, value, value_max, value_label, title):
            c.create_text(x0, y, anchor="nw", text=title, fill="#8888bb", font=("Segoe UI", 8))
            if value_label:
                c.create_text(x0 + w, y, anchor="ne", text=value_label,
                               fill="white", font=("Segoe UI", 8, "bold"))
            by0 = y + 13
            by1 = by0 + h
            if value_max and value_max > 0:
                prev_x = x0
                for frac_end, color in zones:
                    zx = x0 + w * min(max(frac_end, 0.0), 1.0)
                    c.create_rectangle(prev_x, by0, zx, by1, fill=color, outline="")
                    prev_x = zx
                if value is not None:
                    frac = max(0.0, min(1.0, value / value_max))
                    mx = x0 + w * frac
                    c.create_line(mx, by0 - 2, mx, by1 + 2, fill="white", width=2)
            else:
                c.create_rectangle(x0, by0, x0 + w, by1, fill="#141420", outline="")
                c.create_text(x0 + w / 2, (by0 + by1) / 2, text="n/a",
                               fill="#555577", font=("Segoe UI", 8))
            return by1

        electrons = meter.get("electrons")
        target    = meter.get("target")
        sat_pct   = meter.get("sat_pct")
        sat_basis = meter.get("sat_basis")

        y = 2
        if target:
            scale_max = max(target * self._TARGET_HIGH_MULT * 1.15, (electrons or 0) * 1.1, 1.0)
            zones = [
                (self._TARGET_LOW_FRAC * target / scale_max,  "#8a5a1a"),   # low
                (self._TARGET_HIGH_MULT * target / scale_max, "#1a6a35"),   # good
                (1.0,                                           "#1a4a7a"),  # excess
            ]
            label = f"{electrons:,.0f} / {target:,.0f} e-" if electrons is not None else None
            y = draw_bar(y, zones, electrons, scale_max, label, "Signal (total electrons)") + 12
        else:
            c.create_text(x0, y, anchor="nw", text="Signal -- set Target electrons to enable",
                           fill="#8888bb", font=("Segoe UI", 8))
            y += 28

        if sat_pct is not None:
            zones = [
                (0.70,                                 "#1a6a35"),   # safe
                (self._SATURATION_WARN_PCT / 100.0,     "#8a5a1a"),   # caution
                (1.0,                                   "#7a1a1a"),   # danger
            ]
            label = f"{sat_pct:.0f}% of {sat_basis}"
            draw_bar(y, zones, sat_pct, 100.0, label, "Saturation (peak)")
        else:
            c.create_text(x0, y, anchor="nw", text="Saturation -- no BITPIX / Full well set",
                           fill="#8888bb", font=("Segoe UI", 8))

    def _get_exptime(self):
        """Exposure time in seconds from the FITS header, or None."""
        if self.fits_header is None:
            return None
        for key in ("EXPTIME", "EXPOSURE"):
            val = self.fits_header.get(key)
            if val is not None:
                try:
                    return float(val)
                except (TypeError, ValueError):
                    pass
        return None

    def _evaluate_exposure_meter(self, peak: float, peak_electrons, sky_median: float,
                                  sky_sigma: float, electrons):
        """Combined exposure-quality read for the panel: saturation headroom,
        SNR floor, and total-electron target, folded into one recommendation.

        Priority (highest first): too faint to measure reliably, near
        saturation/non-linearity, under the electron target, over the electron
        target, otherwise good. Saturation is checked against Full well (e-)
        when the user has supplied one -- this works on calibrated/float files,
        where the BITPIX-derived ADC-ceiling proxy below is unavailable -- and
        falls back to the ADC-ceiling proxy otherwise.
        """
        snr = ((peak - sky_median) / sky_sigma) if sky_sigma > 0 else None

        sat_pct, sat_basis = None, None
        full_well_str = self.full_well.get().strip()
        if full_well_str and peak_electrons is not None:
            try:
                full_well_e = float(full_well_str)
                if full_well_e > 0:
                    sat_pct, sat_basis = peak_electrons / full_well_e * 100.0, "full well"
            except ValueError:
                pass
        if sat_pct is None:
            ceiling = self._saturation_ceiling()
            if ceiling:
                sat_pct, sat_basis = peak / ceiling * 100.0, "ADC ceiling"

        target_e = None
        target_str = self.target_electrons.get().strip()
        if target_str:
            try:
                target_e = float(target_str)
                if target_e <= 0:
                    target_e = None
            except ValueError:
                target_e = None

        exptime = self._get_exptime()

        def suggest(new_electrons_ratio):
            """'try ~Xs' text if we know the current exposure time, else ''."""
            if exptime is None or new_electrons_ratio is None or new_electrons_ratio <= 0:
                return ""
            return f" -- try ~{exptime * new_electrons_ratio:.1f}s"

        common = dict(electrons=electrons, target=target_e, sat_pct=sat_pct, sat_basis=sat_basis)

        if snr is not None and snr < self._LOW_SNR_THRESHOLD:
            return dict(state="TOO FAINT", color="#ff5555",
                        detail=f"peak only {snr:.1f}x sky noise",
                        recommendation="Increase exposure -- signal is barely above the noise floor.",
                        **common)

        if sat_pct is not None and sat_pct >= self._SATURATION_WARN_PCT:
            ratio = (70.0 / sat_pct) if sat_pct > 0 else None
            return dict(state="NEAR SATURATION", color="#ff5555",
                        detail=f"{sat_pct:.0f}% of {sat_basis}",
                        recommendation=f"Reduce exposure{suggest(ratio)}.",
                        **common)

        if target_e is not None and electrons is not None:
            if electrons < self._TARGET_LOW_FRAC * target_e:
                ratio = (target_e / electrons) if electrons > 0 else None
                sat_note = f"; {sat_pct:.0f}% of {sat_basis}, room to grow" if sat_pct is not None else ""
                return dict(state="LOW SIGNAL", color="#ffcc55",
                            detail=f"{electrons:,.0f} e- vs {target_e:,.0f} target{sat_note}",
                            recommendation=f"Increase exposure{suggest(ratio)}.",
                            **common)
            if electrons > self._TARGET_HIGH_MULT * target_e:
                ratio = (target_e / electrons) if electrons > 0 else None
                return dict(state="EXCESS SIGNAL", color="#66aaff",
                            detail=f"{electrons:,.0f} e- vs {target_e:,.0f} target",
                            recommendation=f"Past the photon-noise floor -- shorter exposures would "
                                           f"trade unneeded precision for better cadence{suggest(ratio)}.",
                            **common)

        detail_bits = []
        if electrons is not None and target_e is not None:
            detail_bits.append(f"{electrons:,.0f} e- (target {target_e:,.0f})")
        elif electrons is not None:
            detail_bits.append(f"{electrons:,.0f} e-")
        if sat_pct is not None:
            detail_bits.append(f"{sat_pct:.0f}% of {sat_basis}")
        if not detail_bits:
            detail_bits.append("no BITPIX / gain not set")
        return dict(state="GOOD", color="#55dd77",
                    detail="; ".join(detail_bits),
                    recommendation="No change needed.",
                    **common)

    # -----------------------------------------------------------------------
    # Header Panel
    # -----------------------------------------------------------------------
    def _toolbar_toggle_header(self):
        # Always show — only the window's X button closes it
        self.header_visible.set(True)
        self.toggle_header()

    def toggle_header(self):
        if self.header_visible.get():
            if self.header_win is None:
                self._create_header_window()
            else:
                self.header_win.deiconify()
            if self.fits_header is not None:
                self.populate_header()
        else:
            if self.header_win is not None:
                self.header_win.withdraw()

    def populate_header(self):
        self.header_tree.delete(*self.header_tree.get_children())
        self._header_items.clear()
        if self.fits_header is None:
            return
        for card in self.fits_header.cards:
            kw  = str(card.keyword)
            val = str(card.value)
            com = str(card.comment)
            iid = self.header_tree.insert("", "end", values=(kw, val, com))
            self._header_items[iid] = [kw, val, com]

    def _filter_header(self):
        term = self.header_search.get().lower()
        self.header_tree.delete(*self.header_tree.get_children())
        self._header_items.clear()
        if self.fits_header is None:
            return
        for card in self.fits_header.cards:
            kw  = str(card.keyword)
            val = str(card.value)
            com = str(card.comment)
            if term and term not in kw.lower() and term not in val.lower() and term not in com.lower():
                continue
            iid = self.header_tree.insert("", "end", values=(kw, val, com))
            self._header_items[iid] = [kw, val, com]

    def _on_header_double_click(self, event):
        item = self.header_tree.identify_row(event.y)
        col  = self.header_tree.identify_column(event.x)
        if not item or col not in ("#1", "#2", "#3"):
            return

        col_idx = int(col[1:]) - 1
        col_names = ("Keyword", "Value", "Comment")
        vals = list(self.header_tree.item(item, "values"))
        current = vals[col_idx]

        new_val = simpledialog.askstring(
            "Edit Header",
            f"Edit {col_names[col_idx]} for  {vals[0]}:",
            initialvalue=current,
            parent=self.header_win
        )
        if new_val is None:
            return

        vals[col_idx] = new_val
        self.header_tree.item(item, values=vals)
        if item in self._header_items:
            self._header_items[item][col_idx] = new_val

        # Push change into the in-memory header object
        keyword = vals[0]
        value   = vals[1]
        comment = vals[2]
        try:
            if keyword not in ("COMMENT", "HISTORY", ""):
                typed = self._coerce_value(value)
                self.fits_header[keyword] = (typed, comment)
        except Exception as exc:
            messagebox.showerror("Header Update Error", str(exc))

    @staticmethod
    def _coerce_value(s: str):
        """Try to convert a string to int, float, bool, or leave as str."""
        try:
            return int(s)
        except ValueError:
            pass
        try:
            return float(s)
        except ValueError:
            pass
        if s.upper() in ("T", "TRUE"):
            return True
        if s.upper() in ("F", "FALSE"):
            return False
        return s

    def _add_header_card(self):
        if self.fits_header is None:
            return
        kw = simpledialog.askstring("Add Card", "Keyword:", parent=self.header_win)
        if not kw:
            return
        kw = kw.upper().strip()[:8]
        val_str = simpledialog.askstring("Add Card", "Value:", parent=self.header_win) or ""
        com     = simpledialog.askstring("Add Card", "Comment:", parent=self.header_win) or ""
        try:
            self.fits_header[kw] = (self._coerce_value(val_str), com)
            self.populate_header()
        except Exception as exc:
            messagebox.showerror("Error", str(exc))

    def _delete_header_card(self):
        sel = self.header_tree.selection()
        if not sel:
            return
        item = sel[0]
        vals = self.header_tree.item(item, "values")
        kw   = vals[0]
        if kw in ("SIMPLE", "BITPIX", "NAXIS", "NAXIS1", "NAXIS2", "EXTEND", "END"):
            messagebox.showwarning("Cannot Delete",
                                   f'"{kw}" is a required FITS keyword.',
                                   parent=self.header_win)
            return
        if messagebox.askyesno("Delete Card", f'Delete keyword "{kw}"?',
                               parent=self.header_win):
            try:
                del self.fits_header[kw]
                self.header_tree.delete(item)
                self._header_items.pop(item, None)
            except Exception as exc:
                messagebox.showerror("Error", str(exc), parent=self.header_win)

    def save_header(self):
        if not self.fits_file or self.fits_header is None:
            messagebox.showwarning("No File", "No FITS file is loaded.")
            return
        if not messagebox.askyesno(
            "Save Header",
            f"Save header changes to:\n{self.fits_file}\n\nThis modifies the file in-place."
        ):
            return
        try:
            with fits.open(self.fits_file, mode="update") as hdul:
                for h in hdul:
                    if h.data is not None and h.data.ndim >= 2:
                        h.header.update(self.fits_header)
                        break
                hdul.flush()
            messagebox.showinfo("Saved", "Header saved successfully.")
        except Exception as exc:
            messagebox.showerror("Save Error", str(exc))

    # -----------------------------------------------------------------------
    # Video Playback
    # -----------------------------------------------------------------------
    def _update_playback_ui(self):
        n = len(self.sequence_files)
        if n <= 1:
            self.frame_slider.config(to=max(0, n - 1), state="disabled")
        else:
            self.frame_slider.config(to=n - 1, state="normal")
        self._update_frame_label()

    def _update_frame_label(self):
        n = len(self.sequence_files)
        if n == 0:
            self.frame_label.config(text="Frame: -/-")
        else:
            self.frame_label.config(text=f"Frame: {self.current_frame + 1}/{n}")

    def _on_frame_slider(self, val):
        idx = int(float(val))
        if not (0 <= idx < len(self.sequence_files)):
            return
        self.current_frame = idx
        self._update_frame_label()
        # Debounce: only load the file after dragging pauses for 150 ms
        if self._frame_slider_after_id is not None:
            self.root.after_cancel(self._frame_slider_after_id)
        self._frame_slider_after_id = self.root.after(
            150, lambda i=idx: self._load_frame_from_slider(i)
        )

    def _load_frame_from_slider(self, idx):
        self._frame_slider_after_id = None
        if 0 <= idx < len(self.sequence_files):
            self.load_fits(self.sequence_files[idx], auto=False)

    def frame_first(self):
        if self.sequence_files:
            self.current_frame = 0
            self.frame_var.set(0)
            self.load_fits(self.sequence_files[0], auto=False)
            self._update_frame_label()

    def frame_last(self):
        if self.sequence_files:
            self.current_frame = len(self.sequence_files) - 1
            self.frame_var.set(self.current_frame)
            self.load_fits(self.sequence_files[self.current_frame], auto=False)
            self._update_frame_label()

    def frame_prev(self):
        if self.sequence_files and self.current_frame > 0:
            self.current_frame -= 1
            self.frame_var.set(self.current_frame)
            self.load_fits(self.sequence_files[self.current_frame], auto=False)
            self._update_frame_label()

    def frame_next(self):
        if self.sequence_files and self.current_frame < len(self.sequence_files) - 1:
            self.current_frame += 1
            self.frame_var.set(self.current_frame)
            self.load_fits(self.sequence_files[self.current_frame], auto=False)
            self._update_frame_label()

    def toggle_play(self):
        if self.playing:
            self.playing = False
            self.play_btn.config(text="▶")
            if self._play_after_id is not None:
                self.root.after_cancel(self._play_after_id)
                self._play_after_id = None
            self._redraw_histogram()
        else:
            if len(self.sequence_files) < 2:
                return
            self.playing = True
            self.play_btn.config(text="⏸")
            # Drain any stale frames from a previous play session
            while not self._play_queue.empty():
                try:
                    self._play_queue.get_nowait()
                except queue.Empty:
                    break
            # Start background loader thread then display loop
            self._play_next_frame_time = time.perf_counter()
            threading.Thread(target=self._play_loader_thread, daemon=True).start()
            self._play_display_loop()

    def _play_loader_thread(self):
        """Background thread: load and process FITS frames, put results in queue."""
        n = len(self.sequence_files)
        idx = self.current_frame
        while self.playing:
            idx = (idx + 1) % n
            try:
                with fits.open(self.sequence_files[idx]) as hdul:
                    hdu = next(
                        (h for h in hdul if h.data is not None and h.data.ndim >= 2),
                        None
                    )
                    if hdu is None:
                        continue
                    data = hdu.data.astype(np.float64)
                    header = hdu.header.copy()
                if data.ndim == 3:
                    data = data[0]

                dmin, dmax = self._data_range
                drange = max(dmax - dmin, 1e-10)
                bl = self.black_level.get()
                wl = self.white_level.get()
                if wl <= bl:
                    wl = bl + 1e-6
                vmin = dmin + bl * drange
                vmax = dmin + wl * drange
                norm = np.clip((data - vmin) / (vmax - vmin), 0.0, 1.0)
                rgb = self._apply_colormap(norm, self.colormap.get())
                img = Image.fromarray(rgb, mode="RGB")

                self._play_queue.put((idx, data, header, img), timeout=2.0)
            except queue.Full:
                pass
            except Exception:
                break

    def _play_display_loop(self):
        """Main thread: dequeue pre-loaded frames and update the canvas.

        Fixed at 1 fps — one frame per second is reliable within Windows timer
        granularity (~15 ms error out of 1000 ms = < 2%).
        """
        if not self.playing:
            return
        frame_duration = 1.0   # 1 fps

        try:
            idx, data, header, img = self._play_queue.get_nowait()
            self.current_frame = idx
            self.frame_var.set(idx)
            self.fits_data   = data
            self.fits_header = header
            self.fits_file   = self.sequence_files[idx]
            self.display_image = img
            self._redraw_canvas()
            self._update_frame_label()
            self.sb_file.config(text=os.path.basename(self.sequence_files[idx]))
            self._update_title(os.path.basename(self.sequence_files[idx]))
            h, w = data.shape
            self.sb_dims.config(text=f"{w} × {h} px")
            if self.header_visible.get():
                self.populate_header()
            if self.aperture_center is not None:
                self._compute_photometry()
        except queue.Empty:
            pass  # Loader hasn't caught up; skip this tick

        # Advance the wall-clock deadline by one frame period
        self._play_next_frame_time += frame_duration
        # Compute delay to next deadline; clamp to 1 ms minimum
        delay_s = self._play_next_frame_time - time.perf_counter()
        if delay_s < 0:
            # We're running behind — reset reference to now so we don't spiral
            self._play_next_frame_time = time.perf_counter()
            delay_s = 0.0
        delay_ms = max(1, int(delay_s * 1000))
        self._play_after_id = self.root.after(delay_ms, self._play_display_loop)



# ---------------------------------------------------------------------------
def main():
    root = tk.Tk()
    app = FITSViewer(root)
    # If a file path was passed (e.g. double-clicked in Explorer), open it
    # Use root.after so the window is fully rendered before loading
    if len(sys.argv) > 1:
        path = sys.argv[1]
        if os.path.isfile(path):
            def _open_on_start():
                app.sequence_files = [path]
                app.current_frame  = 0
                app.load_fits(path, auto=True)
                app._update_playback_ui()
            root.after(200, _open_on_start)
    root.mainloop()


if __name__ == "__main__":
    main()
