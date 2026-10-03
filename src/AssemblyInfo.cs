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
