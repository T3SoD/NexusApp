========================================
  NEXUS — Star Citizen Mining Assistant
========================================

DISCLAIMER
----------

  Nexus is an unofficial fan-made tool. It is NOT affiliated with,
  endorsed by, or sponsored by Cloud Imperium Games (CIG) or Roberts
  Space Industries (RSI). Star Citizen®, Roberts Space Industries®
  and Cloud Imperium® are registered trademarks of Cloud Imperium
  Rights LLC.

  This app reads pixel data from your screen and provides reference
  information from a local database. The optional Session Tracking and
  Blueprint Network features additionally read Star Citizen's Game.log
  (read-only) to auto-collect blueprints and to pre-fill your RSI handle
  for a shared-library export. It does NOT read game memory, inject code,
  or modify game files. It is EAC-Safe (Easy Anti-Cheat compatible) and
  operates entirely outside the game process.


GETTING STARTED
---------------

1. Right-click the ZIP file and select "Extract All..."
2. Choose a location (Desktop or Documents works fine)
3. Open the extracted folder
4. Double-click "NexusApp.exe" to launch the app

No installation required. No internet connection required.
The app stores your settings and work orders locally on your PC.


WINDOWS SMARTSCREEN WARNING
----------------------------

  When you first run Nexus you may see a blue "Windows protected your
  PC" dialog from Microsoft SmartScreen. This appears because the app
  is unsigned (code signing certificates cost several hundred dollars
  per year). The app is safe — here is how to proceed:

  Option A — Run anyway:
    1. Click "More info" in the SmartScreen dialog
    2. Click "Run anyway"

  Option B — Unblock before running (recommended if you use ZIP):
    1. Right-click the downloaded ZIP file
    2. Select "Properties"
    3. At the bottom of the General tab, check "Unblock"
    4. Click OK, then extract and run as normal

  If Windows Defender flags the file as a threat, it is a false
  positive. You can submit the file for analysis at:
  security.microsoft.com — or add an exclusion for the Nexus folder
  in Windows Security > Virus & threat protection > Exclusions.


FIRST TIME SETUP
----------------

When the app opens you will land on the RS SIGNAL DECODER page.
To use the auto-scan overlay:

  1. Click the overlay button (⧉) in the top-right of the main window
  2. Switch to the SCAN tab in the overlay
  3. Click ⊕ to draw a scan region over the RS value on your game screen
  4. Click ■ to start scanning — the overlay reads the value automatically


PAGES
-----

  OPERATIONS         - At-a-glance dashboard of your last scan, refinery
                       queue, cargo in transit, session blueprints, and
                       network coverage, with links into every module

  RS SIGNAL DECODER  - Manually enter or auto-scan an RS value to identify
                       the resource and node count

  BLUEPRINT LIBRARY  - Search for ship/weapon/armor blueprints and see
                       which raw resources each one requires

  BLUEPRINT NETWORK  - Share your owned blueprints with friends/org by
                       trading library files, and see who owns what
                       (coverage, gaps, single-owner risk). Fully offline.

  MINING CODEX       - Full reference table of all mineable resources,
                       filterable by system (Stanton / Pyro / Nyx) and
                       mining method (Ship / ROC / FPS)

  REFINERY TRACKER   - Track active refinery jobs with live countdown
                       timers and status indicators

  CARGO HAULING      - Track hauling contracts read from your Game.log,
                       with a consolidation view of what to load and drop
                       at each stop across your active hauls


TIPS
----

  - The overlay (⧉) floats over your game and can be repositioned
    by dragging the NEXUS header bar
  - Use the opacity slider in the SCAN tab to adjust overlay transparency
  - Resources in your shopping list are highlighted with a teal glow
    in scan results and recent scan history
  - Shopping list: add resources or blueprint ingredients with the
    cart (🛒) button and review them anytime from the main toolbar
  - Work order timers survive app restarts


SUPPORT
-------

  Found a bug or have feedback? Open an issue on GitHub:
    https://github.com/T3SoD/NexusApp/issues
  or reach T3SoD on Discord.

========================================
