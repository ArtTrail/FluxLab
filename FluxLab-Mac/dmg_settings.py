# dmgbuild settings file, shared by both the arm64 and x64 FluxLab build scripts.
# Invoked as: dmgbuild -s dmg_settings.py -D app=... -D readme=... -D license=... "<volname>" out.dmg
# Note: __file__ is not available in dmgbuild's exec'd settings context, so every
# path must be passed in via -D defines.
import os.path

application = defines.get('app')
readme = defines.get('readme')
license_file = defines.get('license')
appname = os.path.basename(application)

format = 'UDZO'
files = [f for f in [application, readme, license_file] if f]
symlinks = {'Applications': '/Applications'}

background = defines.get('background')

show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False

window_rect = ((200, 120), (660, 400))
default_view = 'icon-view'
icon_size = 100

icon_locations = {
    appname: (160, 175),
    'Applications': (500, 175),
}
