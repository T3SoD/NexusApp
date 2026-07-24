========================================
  NEXUS - Star Citizen Mining Assistant
========================================

DISCLAIMER
----------

  NexusApp is an unofficial fan-made tool. NexusApp is NOT affiliated
  with Cloud Imperium Games (CIG) or Roberts Space Industries (RSI).
  CIG and RSI do NOT endorse or sponsor NexusApp. Star Citizen®,
  Roberts Space Industries® and Cloud Imperium® are registered
  trademarks of Cloud Imperium Rights LLC.

  NexusApp reads pixel data from your screen. NexusApp shows reference
  information from a local database.

  Session Tracking, the hauling tracker, and the shard tracker are
  always on. They also read Star Citizen's Game.log. These features
  read Game.log only. They do not change Game.log. They read Game.log
  for these reasons:
    - to collect blueprints automatically
    - to track your hauling contracts
    - to track your server and shard
    - to fill in your RSI handle for a shared-library export

  NexusApp does NOT do these things:
    - read game memory
    - inject code
    - change game files

  NexusApp is EAC-Safe (Easy Anti-Cheat compatible). NexusApp runs
  entirely outside the game process.


GETTING STARTED
---------------

1. Right-click the ZIP file.
2. Select "Extract All...".
3. Choose a location. The Desktop or the Documents folder works well.
4. Open the extracted folder.
5. Double-click "NexusApp.exe" to start NexusApp.

NexusApp needs no installation. NexusApp needs no internet connection.
NexusApp stores your settings and work orders on your PC.


WINDOWS SMARTSCREEN WARNING
----------------------------

  When you first start NexusApp, Microsoft SmartScreen can show a blue
  "Windows protected your PC" dialog. This dialog appears because
  NexusApp is unsigned. Code-signing certificates cost several hundred
  dollars each year. NexusApp is safe. Use one of these two options:

  Option A - Run anyway:
    1. Click "More info" in the SmartScreen dialog.
    2. Click "Run anyway".

  Option B - Unblock the file first (recommended for the ZIP file):
    1. Right-click the downloaded ZIP file.
    2. Select "Properties".
    3. In the General tab, check "Unblock" at the bottom.
    4. Click OK.
    5. Extract the ZIP file.
    6. Start NexusApp as usual.

  If Windows Defender flags the file as a threat, the result is a
  false positive. You can submit the file for analysis at
  security.microsoft.com. You can also add an exclusion for the
  NexusApp folder in Windows Security > Virus & threat protection >
  Exclusions.


FIRST TIME SETUP
----------------

When NexusApp opens, it shows the OPERATIONS page first.
To use the auto-scan overlay, do these steps:

  1. Click the overlay button (⧉) at the top-right of the main window.
  2. Go to the SCAN tab in the overlay.
  3. Click ⊕ to draw a scan region over the RS value on your game screen.
  4. Click ■ to start the scan. The overlay reads the value
     automatically.


PAGES
-----

  OPERATIONS         - Dashboard for your last scan, refinery queue,
                       cargo in transit, session blueprints, and
                       network coverage. It links to every module.

  RS SIGNAL DECODER  - Enter an RS value by hand, or use auto-scan.
                       NexusApp then shows the resource and the node
                       count. Each result card shows a composition
                       section (CAN CONTAIN) and the best refinery
                       with its yield bonus.

  BLUEPRINT LIBRARY  - Search for ship, weapon, armor, and ammo
                       blueprints. Select a blueprint to see its
                       bill of materials and how to unlock it. A
                       ranked WHERE TO MINE plan shows the best
                       places to gather the resources, with
                       byproduct sourcing.

  BLUEPRINT NETWORK  - Share your blueprints with your friends or
                       your org. You trade library files to do this.
                       See who owns which blueprints, with coverage,
                       gaps, and single-owner risk. This feature works
                       fully offline.

  MINING CODEX       - Full reference table of all mineable resources.
                       You can filter it by system (Stanton, Pyro, or
                       Nyx) and by mining method (Ship, ROC, or FPS).
                       Select a resource to open its dossier. The
                       dossier shows the resource class (Metal,
                       Mineral, or Gem), a mining profile, the rock
                       composition, byproduct sourcing, locations,
                       blueprints, and refinery yields.

  REFINERY TRACKER   - Track your active refinery jobs. NexusApp shows
                       live countdown timers and status indicators.

  CARGO HAULING      - Track your hauling contracts. NexusApp reads
                       them from your Game.log. A consolidation view
                       shows what to load and what to drop at each stop
                       across your active hauls.


TIPS
----

  - The overlay (⧉) stays on top of your game. To move the overlay,
    drag the NEXUS header bar.
  - Use the opacity slider in the overlay header to change the overlay
    transparency.
  - The overlay click-through is on by default. In FPS and flight, the
    overlay passes the mouse through to the game while the game hides
    the cursor. You can turn this off in Settings.
  - NexusApp marks resources in your shopping list with a teal glow.
    This glow appears in scan results and in recent scan history.
  - Shopping list: add resources or blueprint ingredients with the
    cart button. You can review them at any time from the main
    toolbar.
  - Work order timers keep running after you restart NexusApp.


SUPPORT
-------

  To report a bug or to send feedback, open an issue on GitHub:
    https://github.com/T3SoD/NexusApp/issues
  You can also reach T3SoD on Discord.

========================================
