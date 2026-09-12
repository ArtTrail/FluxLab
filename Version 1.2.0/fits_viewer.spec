# -*- mode: python ; coding: utf-8 -*-
from PyInstaller.utils.hooks import collect_data_files

added_datas  = collect_data_files('astropy',          excludes=['**/tests/**', '**/test_*'])
added_datas += collect_data_files('astropy_iers_data')
added_datas += collect_data_files('asdf')
added_datas += collect_data_files('asdf_astropy')

a = Analysis(
    ['fits_viewer.py'],
    pathex=[],
    binaries=[],
    datas=added_datas,
    hiddenimports=[
        'astropy.io.fits',
        'astropy.io.fits.hdu',
        'astropy.io.fits.hdu.base',
        'astropy.io.fits.hdu.image',
        'astropy.io.fits.hdu.table',
        'astropy.io.fits.hdu.compressed.compressed',
        'astropy.visualization',
        'astropy.visualization.interval',
        'astropy.visualization.stretch',
        'astropy.wcs',
        'astropy.wcs.wcs',
        'astropy.coordinates',
        'astropy.units',
        'astropy.io.registry',
        'astropy.io.registry.core',
        'matplotlib.backends.backend_tkagg',
        'matplotlib.backends.backend_agg',
        'PIL._tkinter_finder',
    ],
    excludes=[
        'numba', 'llvmlite', 'exotic', 'scipy', 'pandas',
        'cv2', 'imageio', 'IPython', 'notebook', 'pytest',
        'pandas_market_calendars',
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    name='Simple FITS Viewer',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=False,
    disable_windowed_traceback=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    name='Simple FITS Viewer',
)
