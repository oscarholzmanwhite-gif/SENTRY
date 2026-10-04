
# SENTRY
Survey and Early Notification of Threatening Reentry  
Now with impact consequences!

## Features
- Career mode only: deducts reputation when asteroids or comets hit the home body. Earlier detection reduces the penalty
- Career mode only: one-time reputation bonus when an asteroid or comet that was heading for impact on its own is deflected
- Career mode only: destroys facilities when large impacts happen near the KSC. This is exceedingly rare and probably won't happen naturally
- Per-game difficulty settings
- Patches stock asteroids and comets to have 30x density (and mass), to align more closely with real asteroids and to make impacts scarier
- Alert window
- Toolbar icon turns red when an impact would cost significant reputation
- Auto-tracks, and alerts you to, threatening or interesting asteroids and comets
- Filter by asteroids, comets, flybys, and impacts
- Audio alerts for important events
- Stock Alarm Clock integration to stop time warp before asteroids hit the home body
- Stop time warp when a threatening asteroid is discovered

## Before you get started
### This mod makes asteroids and comets 30x heavier.
If you want stock masses, delete `GameData/SENTRY/30xCometAsteroidDensity/`. This mod also deducts reputation for asteroid and comet impacts. You can disable that in the SENTRY tab of the game's difficulty settings.
### If you have Kopernicus installed
This works with both the stock asteroid system and the Kopernicus asteroid system, but **you won't get comets with the Kopernicus asteroid system, which is active by default if you have Kopernicus installed** (Kopernicus is a dependency of most planet packs and of Parallax Continued). If you're playing in a system with Kerbin, you probably want the stock asteroid system. You can switch to it by changing  
`UseKopernicusAsteroidSystem = True`  
to  
`UseKopernicusAsteroidSystem = Stock`  
in `GameData/Kopernicus/Config/01_DefaultConfig.cfg`

## Dependencies
- [ClickThroughBlocker](https://github.com/linuxgurugamer/ClickThroughBlocker)
- [ToolbarController](https://github.com/linuxgurugamer/ToolbarControl)
- [ModuleManager](https://github.com/sarbian/ModuleManager) for the 30x density patch

## Installation
- Install all dependencies
- Extract the zip
- Put the `SENTRY/` folder from the zip's `GameData/` folder in the `GameData/` folder of your KSP install

## Known bugs and limitations
- If the stock Alarm Clock integration is turned off, asteroids can clip through the home body at high time warp
- Stock comets can fragment into many smaller comets if they exceed a pressure limit. This throws off the impact consequences a bit
- SENTRY force-loads asteroids to find their masses, which can cause some log spam
- The stock spawner doesn't spawn asteroids without a level 3 Tracking Station. SENTRY also doesn't apply penalties, bonuses, or facility damage without a level 3 Tracking Station
- I've attempted to make penalties similar regardless of whether an asteroid hits while loaded, clawed, or unloaded, but there is still some variance

## AI
I used Claude Code for nearly all of the plugin code and for some other purposes. I'm not proficient with C# or familiar with the KSP codebase. That said, this isn't slop. Human thought, planning, and testing went into this.

## License
MIT - see [LICENSE](LICENSE)