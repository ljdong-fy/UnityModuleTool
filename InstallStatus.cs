using System.Text.RegularExpressions;

namespace UnityModuleTool;

enum InstallStatus { Unknown, Missing, Present, Review }

static class InstallCheck
{
    internal static InstallStatus Inspect(EditorInstall editor, Module module)
    {
        try { return InspectCore(editor, module); }
        catch (IOException) { return InstallStatus.Review; }
        catch (UnauthorizedAccessException) { return InstallStatus.Review; }
        catch (ArgumentException) { return InstallStatus.Review; }
    }

    private static InstallStatus InspectCore(EditorInstall editor, Module module)
    {
        if (ModuleService.IsAndroidZip(module))
        {
            var id = module.Id;
            string tail(string prefix) => id[prefix.Length..];
            bool has(string prefix) => id.StartsWith(prefix, StringComparison.Ordinal);
            if (has("android-open-jdk-"))
            {
                var version = tail("android-open-jdk-");
                var expected = version.Split('+')[0];
                if (Regex.Match(version, @"^8u(\d+)-b(\d+)$") is { Success: true } old)
                    expected = $"1.8.0_{old.Groups[1].Value}";
                return Check(editor.Android, "OpenJDK", @"bin\java.exe", "release",
                    text => ReadProperty(text, "JAVA_VERSION") == expected);
            }
            if (id == "android-sdk-ndk-tools")
                return Revision(editor, @"SDK\tools", @"bin\sdkmanager.bat", "26.1.1");
            if (has("android-ndk-"))
            {
                var release = tail("android-ndk-");
                var marker = @"toolchains\llvm\prebuilt\windows-x86_64\bin\clang.exe";
                // Confirm the exact package revision where known; otherwise preserve any existing NDK.
                string? expected = release switch
                {
                    "r19" => "19.0.5232133", "r21d" => "21.3.6528147", "r23b" => "23.1.7779620",
                    "r27c" => "27.2.12479018", _ => null
                };
                return Check(editor.Android, "NDK", marker, "source.properties",
                    text => expected is not null && ReadProperty(text, "Pkg.Revision") == expected);
            }
            if (has("android-sdk-build-tools-"))
            {
                var version = tail("android-sdk-build-tools-");
                return Revision(editor, $@"SDK\build-tools\{version}", "aapt2.exe", version);
            }
            if (has("android-sdk-platform-tools-"))
                return Revision(editor, @"SDK\platform-tools", "adb.exe", tail("android-sdk-platform-tools-"));
            if (has("android-sdk-platforms-"))
            {
                var api = tail("android-sdk-platforms-");
                // These can have several revisions with the same API level. A different revision
                // is still an installed platform; leave it in place instead of replacing it.
                return Check(editor.Android, $@"SDK\platforms\android-{api}", "android.jar", "source.properties",
                    text => ReadProperty(text, "AndroidVersion.ApiLevel") == api);
            }
            if (has("android-sdk-command-line-tools-"))
            {
                var version = tail("android-sdk-command-line-tools-");
                return Revision(editor, $@"SDK\cmdline-tools\{version}", @"bin\sdkmanager.bat", version);
            }
            if (has("cmake-"))
            {
                var version = tail("cmake-");
                return Revision(editor, $@"SDK\cmake\{version}", @"bin\cmake.exe", version);
            }
            // A new or unexpected ZIP layout needs review, not an automatic write.
            return InstallStatus.Review;
        }

        var engines = Path.Combine(editor.Root, @"Editor\Data\PlaybackEngines");
        return module.Id switch
        {
            "android" => Check(engines, "AndroidPlayer", "UnityEditor.Android.Extensions.dll"),
            "webgl" => Check(engines, "WebGLSupport", "UnityEditor.WebGL.Extensions.dll"),
            "ios" => Check(engines, "iOSSupport", "UnityEditor.iOS.Extensions.dll"),
            "windows-il2cpp" => Check(engines, @"windowsstandalonesupport\Variations", @"win64_player_nondevelopment_il2cpp\UnityPlayer.dll")
                is InstallStatus.Present ? InstallStatus.Present : InstallStatus.Unknown,
            _ => InstallStatus.Unknown
        };
    }

    private static InstallStatus Revision(EditorInstall editor, string directory, string marker, string expected) =>
        Check(editor.Android, directory, marker, "source.properties", text => ReadProperty(text, "Pkg.Revision") == expected);

    private static string? ReadProperty(string text, string name) =>
        Regex.Match(text, $@"(?m)^{Regex.Escape(name)}[ \t]*=[ \t]*([^\r\n]*)") is { Success: true } match
            ? match.Groups[1].Value.Trim().Trim('"') : null;

    private static InstallStatus Check(string root, string relative, string marker, string? metadata = null, Func<string, bool>? verify = null)
    {
        var path = root;
        foreach (var part in relative.Split('\\'))
        {
            if (!Regex.IsMatch(part, @"^[A-Za-z0-9_-][A-Za-z0-9_.-]*$"))
                return InstallStatus.Review;
            path = Path.Combine(path, part);
            try
            {
                var flags = File.GetAttributes(path);
                if ((flags & FileAttributes.ReparsePoint) != 0 || (flags & FileAttributes.Directory) == 0)
                    return InstallStatus.Review;
            }
            catch (FileNotFoundException) { return InstallStatus.Missing; }
            catch (DirectoryNotFoundException) { return InstallStatus.Missing; }
        }
        if (!File.Exists(Path.Combine(path, marker))) return InstallStatus.Review;
        if (metadata is null) return InstallStatus.Present;
        var file = Path.Combine(path, metadata);
        return File.Exists(file) && verify!(File.ReadAllText(file)) ? InstallStatus.Present : InstallStatus.Review;
    }
}
