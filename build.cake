// Tools needed by cake addins
#tool nuget:?package=ILRepack&version=2.0.13
#tool nuget:?package=Cake.MonoApiTools&version=3.0.1
#tool nuget:?package=Microsoft.DotNet.BuildTools.GenAPI&version=1.0.0-beta-00081
#tool nuget:?package=vswhere

// Cake Addins
#addin nuget:?package=Cake.FileHelpers&version=3.1.0
#addin nuget:?package=Cake.Compression&version=0.1.6
#addin nuget:?package=Cake.MonoApiTools&version=3.0.1
#addin nuget:?package=Xamarin.Nuget.Validator&version=1.1.1

// From Cake.Xamarin.Build, dumps out versions of things
//LogSystemInfo ();

var TARGET = Argument ("t", Argument ("target", "Default"));
var BUILD_CONFIG = Argument ("config", "Release");
var VERBOSITY = (Verbosity) Enum.Parse (typeof(Verbosity), Argument ("v", Argument ("verbosity", "Normal")), true);
var MAX_CPU_COUNT = Int32.Parse(Argument("maxcpucount", "0"));

// Lists all the artifacts and their versions for com.android.support.*
// https://dl.google.com/dl/android/maven2/com/android/support/group-index.xml
// Master list of all the packages in the repo:
// https://dl.google.com/dl/android/maven2/master-index.xml

var NUGET_PRE = "";

// FROM: https://dl.google.com/android/repository/addon2-1.xml
var BUILD_TOOLS_URL = "https://dl-ssl.google.com/android/repository/build-tools_r28-macosx.zip";
var ANDROID_SDK_VERSION = IsRunningOnWindows () ? "v9.0" : "android-28";
var RENDERSCRIPT_FOLDER = "android-8.1.0";
var TF_MONIKER = "monoandroid90";

var REF_DOCS_URL = "https://bosstoragemirror.blob.core.windows.net/android-docs-scraper/ea/ea65204c51cf20873c17c32584f3b12ed390ac55/android-support.zip";

// We grab the previous release's api-info.xml to use as a comparison for this build's generated info to make an api-diff
var BASE_API_INFO_URL = EnvironmentVariable("MONO_API_INFO_XML_URL") ?? "https://github.com/xamarin/AndroidSupportComponents/releases/download/27.1.1-rc/api-info.xml";


var MONODROID_PATH = "/Library/Frameworks/Xamarin.Android.framework/Versions/Current/lib/mandroid/platforms/" + ANDROID_SDK_VERSION + "/";
if (IsRunningOnWindows ()) {
	var vsInstallPath = VSWhereLatest (new VSWhereLatestSettings { Requires = "Component.Xamarin" });
	MONODROID_PATH = vsInstallPath.Combine ("Common7/IDE/ReferenceAssemblies/Microsoft/Framework/MonoAndroid/" + ANDROID_SDK_VERSION).FullPath;
}

var MSCORLIB_PATH = "/Library/Frameworks/Xamarin.Android.framework/Libraries/mono/2.1/";
if (IsRunningOnWindows ()) {

	var DOTNETDIR = new DirectoryPath (Environment.GetFolderPath (Environment.SpecialFolder.Windows)).Combine ("Microsoft.NET/");

	if (DirectoryExists (DOTNETDIR.Combine ("Framework64")))
		MSCORLIB_PATH = MakeAbsolute (DOTNETDIR.Combine("Framework64/v4.0.30319/")).FullPath;
	else
		MSCORLIB_PATH = MakeAbsolute (DOTNETDIR.Combine("Framework/v4.0.30319/")).FullPath;
}

var ANDROIDX_MAPPER_EXE = MakeAbsolute ((FilePath)$"util/AndroidXMapper/AndroidXMapper/bin/{BUILD_CONFIG}/net47/AndroidXMapper.exe");

Information ("MONODROID_PATH: {0}", MONODROID_PATH);
Information ("MSCORLIB_PATH: {0}", MSCORLIB_PATH);

// You shouldn't have to configure anything below here
// ######################################################

Task("javadocs")
	.Does(() =>
{
	if (!FileExists("./externals/docs.zip"))
		DownloadFile(REF_DOCS_URL, "./externals/docs.zip");

	if (!DirectoryExists("./externals/docs"))
		Unzip ("./externals/docs.zip", "./externals/docs");

	var astJar = new FilePath("./util/JavaASTParameterNames-1.0.jar");
	var sourcesJars = GetFiles("./externals/**/*-sources.jar");

	foreach (var srcJar in sourcesJars) {
		var srcJarPath = MakeAbsolute(srcJar).FullPath;
		var outTxtPath = srcJarPath.Replace("-sources.jar", "-paramnames.txt");
		var outXmlPath = srcJarPath.Replace("-sources.jar", "-paramnames.xml");

		StartProcess("java", "-jar \"" + MakeAbsolute(astJar).FullPath + "\" --text \"" + srcJarPath + "\" \"" + outTxtPath + "\"");
		StartProcess("java", "-jar \"" + MakeAbsolute(astJar).FullPath + "\" --xml \"" + srcJarPath + "\" \"" + outXmlPath + "\"");
	}
});

Task("binderate")
	.Does(() =>
{
	var configFile = MakeAbsolute(new FilePath("./config.json")).FullPath;
	var basePath = MakeAbsolute(new DirectoryPath ("./")).FullPath;

	// Run the dotnet tool for binderator
	StartProcess("xamarin-android-binderator",
		$"--config=\"{configFile}\" --basepath=\"{basePath}\"");
});

Task("libs")
	.IsDependentOn("nuget-restore")
	.Does(() =>
{
	NuGetRestore("./generated/AndroidSupport.sln", new NuGetRestoreSettings { });

	MSBuild("./generated/AndroidSupport.sln", c => {
		c.Configuration = "Release";
		c.Properties.Add("DesignTimeBuild", new [] { "false" });
	});
});

Task("nuget-restore")
	.Does(() =>
{
	NuGetRestore("./generated/AndroidSupport.sln", new NuGetRestoreSettings { });
});
Task("nuget")
	.IsDependentOn("libs")
	.Does(() =>
{
	MSBuild ("./generated/AndroidSupport.sln", c => {
		c.Configuration = "Release";
		c.MaxCpuCount = MAX_CPU_COUNT;
		c.Targets.Clear();
		c.Targets.Add("Pack");
		c.Properties.Add("PackageOutputPath", new [] { MakeAbsolute(new FilePath("./output")).FullPath });
		c.Properties.Add("PackageRequireLicenseAcceptance", new [] { "true" });
		c.Properties.Add("DesignTimeBuild", new [] { "false" });
	});
});

Task("nuget-validation")
    .IsDependentOn("nuget")
	.Does(()=>
{
	//setup validation options
	var options = new Xamarin.Nuget.Validator.NugetValidatorOptions()
	{
		Copyright = "© Microsoft Corporation. All rights reserved.",
		Author = "Microsoft",
		Owner = "Microsoft",
		NeedsProjectUrl = true,
		NeedsLicenseUrl = true,
		ValidateRequireLicenseAcceptance = true,
		ValidPackageNamespace = new [] { "Xamarin" },
	};

	var nupkgFiles = GetFiles ("./output/*.nupkg");

	Information ("Found ({0}) Nuget's to validate", nupkgFiles.Count ());

	foreach (var nupkgFile in nupkgFiles)
	{
		Information ("Verifiying Metadata of {0}", nupkgFile.GetFilename ());

		var result = Xamarin.Nuget.Validator.NugetValidator.Validate(MakeAbsolute(nupkgFile).FullPath, options);

		if (!result.Success)
		{
			Information ("Metadata validation failed for: {0} \n\n", nupkgFile.GetFilename ());
			Information (string.Join("\n    ", result.ErrorMessages));
			throw new Exception ($"Invalid Metadata for: {nupkgFile.GetFilename ()}");

		}
		else
		{
			Information ("Metadata validation passed for: {0}", nupkgFile.GetFilename ());
		}
			
	}

});

Task ("androidxmapper")
	.Does (() =>
{
	MSBuild (
		"./util/AndroidXMapper/AndroidXMapper.sln", c => {
		c.Configuration = BUILD_CONFIG;
		c.MaxCpuCount = 0;
		c.Verbosity = VERBOSITY;
		c.Restore = true;
	});
});

Task ("diff")
	.WithCriteria (!IsRunningOnWindows ())
	.IsDependentOn ("merge")
	.Does (() =>
{
	var SEARCH_DIRS = new DirectoryPath [] {
		MONODROID_PATH,
		"/Library/Frameworks/Xamarin.Android.framework/Versions/Current/lib/xbuild-frameworks/MonoAndroid/v1.0/",
		"/Library/Frameworks/Xamarin.Android.framework/Versions/Current/lib/mono/2.1/"
	};

	MonoApiInfo ("./output/AndroidSupport.Merged.dll",
		"./output/AndroidSupport.api-info.xml",
		new MonoApiInfoToolSettings { SearchPaths = SEARCH_DIRS });

	// Grab the last public release's api-info.xml to use as a base to compare and make an API diff
	DownloadFile (BASE_API_INFO_URL, "./output/AndroidSupport.api-info.previous.xml");

	// Now diff against current release'd api info
	// eg: mono mono-api-diff.exe ./gps.r26.xml ./gps.r27.xml > gps.diff.xml
	MonoApiDiff ("./output/AndroidSupport.api-info.previous.xml",
		"./output/AndroidSupport.api-info.xml",
		"./output/AndroidSupport.api-diff.xml");

	// Now let's make a purty html file
	// eg: mono mono-api-html.exe -c -x ./gps.previous.info.xml ./gps.current.info.xml > gps.diff.html
	MonoApiHtml ("./output/AndroidSupport.api-info.previous.xml",
		"./output/AndroidSupport.api-info.xml",
		"./output/AndroidSupport.api-diff.html");
});

Task ("merge")
	.IsDependentOn ("androidxmapper")
	.IsDependentOn ("libs")
	.Does (() =>
{
	var allDlls = GetFiles ($"./generated/*/bin/{BUILD_CONFIG}/{TF_MONIKER}/Xamarin.*.dll");
	var mergeDlls = allDlls
		.GroupBy(d => new FileInfo(d.FullPath).Name)
		.Select(g => g.FirstOrDefault())
		.ToList();

	EnsureDirectoryExists("./output/");
	var result = StartProcess(ANDROIDX_MAPPER_EXE,
		$"merge" +
		$" -a {string.Join(" -a ", mergeDlls)} " +
		$" -o " + MakeAbsolute((FilePath)"./output/AndroidSupport.Merged.dll") +
		$" -s \"{MONODROID_PATH}\" " +
		$" --inject-assemblyname");
	if (result != 0)
		throw new Exception($"The androidxmapper failed with error code {result}.");
});

Task ("ci-setup")
	.WithCriteria (!BuildSystem.IsLocalBuild)
	.Does (() => 
{
	var buildCommit = "DEV";
	var buildNumber = "DEBUG";
	var buildTimestamp = DateTime.UtcNow.ToString ();


	if (BuildSystem.IsRunningOnJenkins) {
		buildNumber = BuildSystem.Jenkins.Environment.Build.BuildTag;
		buildCommit = EnvironmentVariable("GIT_COMMIT") ?? buildCommit;
	} else if (!string.IsNullOrWhiteSpace(EnvironmentVariable("TF_BUILD"))) {
		// Running on AzureDevOps
		buildNumber = BuildSystem.TFBuild.Environment.Build.Number;
		buildCommit = BuildSystem.TFBuild.Environment.Repository.SourceVersion;
	}

	var glob = "./source/AssemblyInfo.cs";

	ReplaceTextInFiles(glob, "{BUILD_COMMIT}", buildCommit);
	ReplaceTextInFiles(glob, "{BUILD_NUMBER}", buildNumber);
	ReplaceTextInFiles(glob, "{BUILD_TIMESTAMP}", buildTimestamp);
});

// Task ("genapi")
// 	.IsDependentOn ("libs")
// 	.Does (() => 
// {
// 	var GenApiToolPath = GetFiles ("./tools/**/GenAPI.exe").FirstOrDefault ();

// 	// For some reason GenAPI.exe can't handle absolute paths on mac/unix properly, so always make them relative
// 	// GenAPI.exe -libPath:$(MONOANDROID) -out:Some.generated.cs -w:TypeForwards ./relative/path/to/Assembly.dll
// 	var libDirPrefix = IsRunningOnWindows () ? "output/" : "";

// 	var libs = new FilePath [] {
// 		"./" + libDirPrefix + "Xamarin.Android.Support.Compat.dll",
// 		"./" + libDirPrefix + "Xamarin.Android.Support.Core.UI.dll",
// 		"./" + libDirPrefix + "Xamarin.Android.Support.Core.Utils.dll",
// 		"./" + libDirPrefix + "Xamarin.Android.Support.Fragment.dll",
// 		"./" + libDirPrefix + "Xamarin.Android.Support.Media.Compat.dll",
// 	};

// 	foreach (var lib in libs) {
// 		var genName = lib.GetFilename () + ".generated.cs";

// 		var libPath = IsRunningOnWindows () ? MakeAbsolute (lib).FullPath : lib.FullPath;
// 		var monoDroidPath = IsRunningOnWindows () ? "\"" + MONODROID_PATH + "\"" : MONODROID_PATH;

// 		Information ("GenAPI: {0}", lib.FullPath);

// 		StartProcess (GenApiToolPath, new ProcessSettings {
// 			Arguments = string.Format("-libPath:{0} -out:{1}{2} -w:TypeForwards {3}",
// 							monoDroidPath + "," + MSCORLIB_PATH,
// 							IsRunningOnWindows () ? "" : "./",
// 							genName,
// 							libPath),
// 			WorkingDirectory = "./output/",
// 		});
// 	}

// 	MSBuild ("./AndroidSupport.TypeForwarders.sln", c => c.Configuration = BUILD_CONFIG);

// 	CopyFile ("./support-v4/source/bin/" + BUILD_CONFIG + "/Xamarin.Android.Support.v4.dll", "./output/Xamarin.Android.Support.v4.dll");
// });

// Task ("buildtasks")
// 	.Does (() => 
// {
// 	NuGetRestore ("./support-vector-drawable/buildtask/Vector-Drawable-BuildTasks.csproj");

// 	MSBuild ("./support-vector-drawable/buildtask/Vector-Drawable-BuildTasks.csproj", c => c.Configuration = BUILD_CONFIG);
// });

Task ("clean")
	.Does (() =>
{
	if (DirectoryExists ("./externals"))
		DeleteDirectory ("./externals", true);

	if (DirectoryExists ("./generated"))
		DeleteDirectory ("./generated", true);

	if (DirectoryExists ("./util/binderator"))
		DeleteDirectory ("./util/binderator", true);

	CleanDirectories ("./**/packages");
});

Task ("ci")
	.IsDependentOn ("ci-setup")
	.IsDependentOn ("binderate")
	.IsDependentOn ("nuget-validation")
	.IsDependentOn ("diff");

RunTarget (TARGET);
