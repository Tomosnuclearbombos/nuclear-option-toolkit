Live map extras for the Nuclear Option Toolkit
==============================================

This is for an install you updated from an older release with the updater, given the updater only
brings the bot, the plugin and the dashboard's own files. A fresh install from the Pterodactyl
download already has all of this.

What's in it:
* map_models\ : the unit icons the live map draws, taken from the game's own models.
* heartland_map.png and ignus_map.png : the sharper map images.
* acmi_live.py : the NOBlackBox live map module.

To install it, update to 1.9.0 first, then unzip this into the toolkit folder (the one with
cc_web.py in it), let it replace the two map images, and restart the dashboard.

Of note, acmi_live.py needs the NOBlackBox recorder on the game server. Without it the module does
nothing and the live map works the same as before. The recorder is my fork of KopterBuzz's
NOBlackBox, and the setup steps are in the toolkit README under "Live map extras and Tacview
replays":

  NOBlackBox fork:  https://github.com/Tomosnuclearbombos/NOBlackBox/releases
  Setup steps:      https://github.com/Tomosnuclearbombos/nuclear-option-toolkit#live-map-extras-and-tacview-replays-optional
