#!/usr/bin/env python3
"""
FITS Viewer - Astronomy image viewer
Features: autostretch, histogram with draggable level controls, video playback,
MP4 export, FITS header readout and editing.
"""

import json
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
APP_VERSION = "1.2.0"

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
        self.root.title(f"Simple FITS Viewer  v{APP_VERSION}")
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

        self._build_ui()
        self._update_hist_zoom_label()   # show last-used zoom level immediately
        self._bind_keys()
        self.root.geometry("1920x1380")
        self.root.minsize(1200, 900)

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
        self.header_win.geometry("672x1300")
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
            "  • Double-click a .fits     — open directly from Explorer")

        section("2.  ZOOM & PAN  (image canvas)", "sec_zoom",
            "  • Scroll wheel             — zoom in / out\n"
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
        ttk.Separator(sb, orient="vertical").pack(side="left", fill="y", pady=2)
        self.sb_pixel = tk.Label(sb, text="", **lkw)
        self.sb_pixel.pack(side="left", padx=8)
        ttk.Separator(sb, orient="vertical").pack(side="left", fill="y", pady=2)
        self.sb_zoom  = tk.Label(sb, text="Zoom: 1.00×", **lkw)
        self.sb_zoom.pack(side="left", padx=8)
        self.sb_dims  = tk.Label(sb, text="", **lkw)
        self.sb_dims.pack(side="right", padx=8)
        tk.Label(sb, text="© Art Trail 2026", **lkw).pack(side="right", padx=12)

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
        self.sequence_files = files
        self.current_frame  = 0
        self.load_fits(files[0], auto=True)
        self._update_playback_ui()

    def load_fits(self, path: str, auto: bool = True):
        """Load image data and header from a FITS file."""
        try:
            with fits.open(path) as hdul:
                hdu = None
                for h in hdul:
                    if h.data is not None and h.data.ndim >= 2:
                        hdu = h
                        break
                if hdu is None:
                    messagebox.showerror("Error", f"No image data in:\n{path}")
                    return

                data = hdu.data.astype(np.float64)
                self.fits_header = hdu.header.copy()

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
            h, w = data.shape
            self.sb_dims.config(text=f"{w} × {h} px")

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
        factor = 1.1 if event.delta > 0 else 0.9
        cx = self.canvas.canvasx(event.x)
        cy = self.canvas.canvasy(event.y)
        old = self.zoom_level
        self.zoom_level = max(0.02, min(64.0, self.zoom_level * factor))
        s = self.zoom_level / old
        self.pan_x = int(cx - s * (cx - self.pan_x))
        self.pan_y = int(cy - s * (cy - self.pan_y))
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

    def _on_canvas_motion(self, event):
        if self.fits_data is None or self.display_image is None:
            return
        cx = self.canvas.canvasx(event.x)
        cy = self.canvas.canvasy(event.y)
        cw = max(self.canvas.winfo_width(),  1)
        ch = max(self.canvas.winfo_height(), 1)
        iw, ih = self.display_image.size
        x0 = max(0, (cw - int(iw * self.zoom_level)) // 2) + self.pan_x
        y0 = max(0, (ch - int(ih * self.zoom_level)) // 2) + self.pan_y
        px = int((cx - x0) / self.zoom_level)
        py = int((cy - y0) / self.zoom_level)
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
            h, w = data.shape
            self.sb_dims.config(text=f"{w} × {h} px")
            if self.header_visible.get():
                self.populate_header()
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
