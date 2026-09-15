
# SENTRY
Survey and Early Notification of Threatening Reentry

A Kerbal Space Program mod that alerts you to asteroids on impact trajectories and incoming comets.

## Features
- Alert window for asteroids and comets
- Filter by asteroids, comets, flybys, and impacts
- Audio alerts for important events
- Stock alarm clock integration to stop timewarp when asteroids hit the home body
- Stop timewarp when a threatening asteroid is discovered

## If you have Kopernicus installed
This should work with the stock asteroid system as well as the Kopernicus asteroid system (though I haven't tested it), but **you won't get comets with the Kopernicus asteroid system, which is active by default if you have Kopernicus installed** (it's a dependency for most planet packs and Parallax Continued). You can switch to the stock asteroid system by changing  
`UseKopernicusAsteroidSystem = True`  
to  
`UseKopernicusAsteroidSystem = Stock`  
in `GameData/Kopernicus/Config/01_DefaultConfig`

## Dependencies
- [Click Through Blocker](https://github.com/linuxgurugamer/ClickThroughBlocker)
- [Toolbar Controller](https://github.com/linuxgurugamer/ToolbarControl)

## Installation
- Install all dependencies
- Copy the `SENTRY` folder to the `GameData` folder of your KSP install

## AI
I used Claude Code for the plugin code. I'm not proficient with C# or the KSP codebase. That said, this isn't slop  human thought, planning, and testing went into this.

## License
MIT - see [LICENSE](LICENSE)