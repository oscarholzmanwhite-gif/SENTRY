using System.Reflection;

// SENTRY.csproj sets GenerateAssemblyInfo=false, so the version lives here. KSP's loader prints it
// in KSP.log and in each save's LoaderInfo node ("SENTRY v2.0.0.0"), which is how a bug report
// shows which build a player is running. Keep in step with SENTRY.version (both copies) on every
// release. KSPAssembly is KSP's own version tag, used by any mod that declares a
// KSPAssemblyDependency on this one.
[assembly: AssemblyTitle("SENTRY")]
[assembly: AssemblyDescription("Survey and Early Notification of Threatening ReentrY")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: KSPAssembly("SENTRY", 2, 0, 0)]

// Code dependencies. KSP's AssemblyLoader (decompiled) loads these before SENTRY, and if either is
// missing or too old it logs "Assembly 'SENTRY' has not met dependency ..." and skips SENTRY.dll
// entirely - a clear message in KSP.log instead of a TypeLoadException the first time the window
// or toolbar button is touched. Names are each mod's own KSPAssembly tag, not its DLL name:
// ToolbarControl.dll declares itself "ToolbarController". Minimum versions match what
// ToolbarController itself requires of ClickThroughBlocker (1.0), so any install that satisfies
// one satisfies both; built and tested against ClickThroughBlocker 2.1.10 / ToolbarController 1.0.1.
//
// ModuleManager is deliberately NOT listed: SENTRY's code never calls it - it's only needed for the
// optional 30xCometAsteroidDensity patch. Declaring it here would stop SENTRY loading at all
// without MM, when the right outcome is SENTRY running at stock density (which the settings tab's
// density line already reports).
[assembly: KSPAssemblyDependency("ClickThroughBlocker", 1, 0)]
[assembly: KSPAssemblyDependency("ToolbarController", 1, 0)]
