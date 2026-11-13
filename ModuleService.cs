using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UnityModuleTool;

record EditorInstall(string Exe, string Version, string Revision, string Architecture)
{
    internal string Root => Path.GetDirectoryName(Path.GetDirectoryName(Exe))!;
    internal string Android => Path.Combine(Root, @"Editor\Data\PlaybackEngines\AndroidPlayer");
    public override string ToString() => $"{Version}   |   {Exe}";

    internal static EditorInstall Read(string path)
    {
        path = Path.GetFullPath(path.Trim().Trim('"'));
        if (Directory.Exists(path))
            path = File.Exists(Path.Combine(path, "Unity.exe")) ? Path.Combine(path, "Unity.exe")
                : Path.Combine(path, @"Editor\Unity.exe");
        if (!File.Exists(path) || !Path.GetFileName(path).Equals("Unity.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Editor", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择 Unity 的安装目录、Editor 目录或 Editor\\Unity.exe。");
        var info = FileVersionInfo.GetVersionInfo(path);
        var match = Regex.Match(info.ProductVersion ?? "", @"^(\d+\.\d+\.\d+[abfp]\d+(?:c\d+)?)(?:[_ +]([a-f0-9]{12}))?", RegexOptions.IgnoreCase);
        if (!match.Success) throw new IOException("无法从 Unity.exe 读取编辑器版本。");
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 0x3c;
        reader.BaseStream.Position = reader.ReadInt32();
        if (reader.ReadUInt32() != 0x4550) throw new IOException("Unity.exe 不是有效的 Windows 程序。");
        var arch = reader.ReadUInt16() switch { 0x8664 => "X86_64", 0xaa64 => "ARM64", _ => throw new IOException("此编辑器架构暂不支持。") };
        return new(path, match.Groups[1].Value, match.Groups[2].Value, arch);
    }

    internal static EditorInstall[] Discover(IEnumerable<string> remembered)
    {
        var found = new Dictionary<string, EditorInstall>(StringComparer.OrdinalIgnoreCase);
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try { var editor = Read(candidate); found[editor.Exe] = editor; } catch { }
        }
        void Scan(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            Add(directory);
            try { foreach (var child in Directory.EnumerateDirectories(directory)) Add(child); } catch { }
        }
        foreach (var path in remembered) Add(path);
        string hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(hub, "editors-v2.json")));
            foreach (var editor in json.RootElement.GetProperty("data").EnumerateArray())
                foreach (var path in editor.GetProperty("location").EnumerateArray()) Add(path.GetString());
        }
        catch { }
        try { Scan(JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(hub, "secondaryInstallPath.json")))); } catch { }
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var keyPath in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = hive.OpenSubKey(keyPath);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(name);
                    if (child?.GetValue("DisplayName") is string display && display.StartsWith("Unity", StringComparison.OrdinalIgnoreCase))
                        Add(child.GetValue("InstallLocation") as string);
                }
            }
            catch { }
        }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            foreach (var relative in new[] { "Unity", @"Software\Unity", @"Program Files\Unity", @"Program Files\Unity\Hub\Editor" })
                Scan(Path.Combine(drive.Name, relative));
        return found.Values.OrderByDescending(e => System.Version.Parse(Regex.Match(e.Version, @"^\d+\.\d+\.\d+").Value))
            .ThenByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Exe).ToArray();
    }
}

record RenameRule(string From, string To);
record Module(string Id, string Name, string Type, string Url, string? Destination, string? Integrity,
    bool Hidden, RenameRule? ExtractedPathRename, Module[]? SubModules)
{
    internal IEnumerable<Module> Flatten() => new[] { this }.Concat((SubModules ?? []).SelectMany(m => m.Flatten()));
    internal bool IsInstaller => Type.Equals("EXE", StringComparison.OrdinalIgnoreCase);
    internal bool IsManualSupport => IsInstaller && Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.Contains("/TargetSupportInstaller/", StringComparison.OrdinalIgnoreCase);
}
record Download(string Platform, string Architecture, Module[]? Modules);
record Release(string Version, string ShortRevision, Download[] Downloads);
record Catalog(Release[] Results);

static class ModuleService
{
    private const string Endpoint = "https://services.api.unity.com/unity/editor/release/v1/releases";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const string AndroidLicense = "https://developer.android.com/studio/terms";

    internal static async Task<Release> Fetch(string version, string arch, CancellationToken ct)
    {
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+[abfp]\d+(?:c\d+)?$"))
            throw new IOException("请输入完整版本号，例如 2022.3.62f3。");
        foreach (var entitlement in new[] { "", "&entitlements=XLTS" })
        {
            using var response = await Http.GetAsync($"{Endpoint}?version={Uri.EscapeDataString(version)}&platform=WINDOWS&architecture={arch}{entitlement}", ct);
            response.EnsureSuccessStatusCode();
            var catalog = JsonSerializer.Deserialize<Catalog>(await response.Content.ReadAsStringAsync(ct), Json);
            var release = catalog?.Results.FirstOrDefault(r => r.Version == version);
            if (release is not null) return release;
        }
        throw new IOException($"Unity 官方发布服务没有找到 {version} 的 Windows {arch} 清单。中国版或团结引擎可能不在这个接口中。");
    }

    internal static Module[] Modules(Release release, string arch) =>
        release.Downloads.Where(d => d.Platform.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase)
            && d.Architecture.Equals(arch, StringComparison.OrdinalIgnoreCase))
        .SelectMany(d => d.Modules ?? []).SelectMany(m => m.Flatten()).DistinctBy(m => m.Id).ToArray();

    internal static bool IsAndroidZip(Module m) => m.Type == "ZIP" &&
        (m.Destination?.StartsWith("{UNITY_PATH}/Editor/Data/PlaybackEngines/AndroidPlayer/", StringComparison.OrdinalIgnoreCase) ?? false);

    internal static void MatchEditor(EditorInstall editor, Release release, string arch)
    {
        if (editor.Version != release.Version || editor.Architecture != arch
            || (editor.Revision.Length > 0 && !editor.Revision.Equals(release.ShortRevision, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("所选 Unity.exe 与官方清单的版本、修订号或架构不匹配。请重新选择。");
    }

    internal static string CachePath(string cache, Release release, string arch, Module module)
    {
        if (!Regex.IsMatch(release.Version, @"^\d+\.\d+\.\d+[abfp]\d+(?:c\d+)?$")
            || !Regex.IsMatch(arch, @"^(X86_64|ARM64)$")) throw new IOException("无效的清单版本或架构。");
        var uri = new Uri(module.Url, UriKind.Absolute);
        if (uri.Scheme != "https") throw new IOException("只支持 HTTPS 下载地址。");
        string name = Uri.UnescapeDataString(uri.Segments.Last()).Trim('/');
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9_.+-]*$") || !Path.HasExtension(name))
        {
            var id = Regex.Replace(module.Id, @"[^A-Za-z0-9_.+-]", "_");
            var type = Regex.Replace(module.Type.ToLowerInvariant(), "[^a-z0-9]", "");
            name = $"module-{id}.{type}";
        }
        return Path.Combine(cache, release.Version, arch, name);
    }

    private static async Task<bool> Valid(string file, Module m, CancellationToken ct)
    {
        if (!File.Exists(file) || new FileInfo(file).Length == 0) return false;
        var parts = m.Integrity?.Split('-', 2);
        if (parts is not { Length: 2 }) return true;
        using HashAlgorithm hash = parts[0] switch
        {
            "md5" => MD5.Create(), "sha256" => SHA256.Create(), "sha384" => SHA384.Create(), "sha512" => SHA512.Create(),
            _ => throw new IOException($"不支持的校验算法：{parts[0]}")
        };
        using var stream = File.OpenRead(file);
        var actual = await hash.ComputeHashAsync(stream, ct);
        var expected = Convert.FromBase64String(parts[1]);
        return actual.SequenceEqual(expected) || Convert.ToHexString(actual).Equals(System.Text.Encoding.ASCII.GetString(expected), StringComparison.OrdinalIgnoreCase);
    }

    internal record DownloadedFile(string Path, bool NewFile);

    internal static async Task<DownloadedFile> DownloadFile(string cache, Release release, string arch, Module m,
        IProgress<(string Label, int Percent)> progress, CancellationToken ct)
    {
        if (m.IsManualSupport) throw new IOException($"{m.Name} 请从对应版本的 Unity 官方发布页手动下载。");
        var path = CachePath(cache, release, arch, m);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bool existed = File.Exists(path);
        if (existed && !string.IsNullOrWhiteSpace(m.Integrity) && await Valid(path, m, ct)) return new(path, false);
        // When Unity supplies no checksum, a receipt ties cache reuse to a download this app completed.
        string receipt = path + ".source";
        if (File.Exists(path) && string.IsNullOrWhiteSpace(m.Integrity) && File.Exists(receipt)
            && await File.ReadAllTextAsync(receipt, ct) == $"{m.Url}\n{new FileInfo(path).Length}") return new(path, false);
        string partial = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            using var response = await GetModuleResponse(m, progress, ct);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new IOException("下载跳转到了非 HTTPS 地址。");
            if (response.Content.Headers.ContentType?.MediaType == "text/html") throw new IOException("服务器返回了网页，未返回模块文件。");
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(partial))
            {
                byte[] buffer = new byte[128 * 1024];
                long copied = 0;
                int count, last = -2;
                while (true)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    try { count = await input.ReadAsync(buffer, timeout.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("下载停滞，请稍后重试。"); }
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    copied += count;
                    var percent = total is > 0 ? (int)Math.Clamp(copied * 100.0 / total.Value, 0, 100) : -1;
                    if (percent != last) { progress.Report(($"下载 {m.Name}", percent)); last = percent; }
                }
                if (total is not null && copied != total) throw new IOException("下载文件大小与服务器报告不符。");
            }
            if (!await Valid(partial, m, ct)) throw new IOException($"下载校验失败：{m.Name}");
            File.Move(partial, path, overwrite: existed);
            if (string.IsNullOrWhiteSpace(m.Integrity))
                await File.WriteAllTextAsync(receipt, $"{m.Url}\n{new FileInfo(path).Length}", ct);
            return new(path, !existed);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static Uri? AlternateUnityCdn(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/download_unity/", StringComparison.Ordinal)) return null;
        var host = uri.Host switch
        {
            "download.unitychina.cn" => "download.unity3d.com",
            "download.unity3d.com" => "download.unitychina.cn",
            _ => null
        };
        return host is null ? null : new UriBuilder(uri) { Host = host }.Uri;
    }

    private static async Task<HttpResponseMessage> GetModuleResponse(Module module,
        IProgress<(string Label, int Percent)> progress, CancellationToken ct)
    {
        var original = new Uri(module.Url);
        var attempted = new List<string>();
        var alternate = AlternateUnityCdn(original);
        foreach (var uri in alternate is null ? new[] { original } : new[] { original, alternate })
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try { response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                attempted.Add($"{uri}（{(ex is OperationCanceledException ? "请求超时" : ex.Message)}）");
                break;
            }
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                var finalUrl = response.RequestMessage?.RequestUri ?? uri;
                attempted.Add($"{uri}{(finalUrl != uri ? $" → {finalUrl}" : "")}（HTTP {(int)response.StatusCode}）");
                if ((int)response.StatusCode is not (403 or 404 or 410) || uri != original ||
                    alternate is null || finalUrl == alternate) break;
                progress.Report(($"{module.Name}：官方节点返回 {(int)response.StatusCode}，尝试另一官方节点", -1));
            }
        }
        throw new IOException($"{module.Name} 下载失败：{string.Join("；", attempted)}");
    }

    private static string Under(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"解压目标越界：{relative}");
        return path;
    }

    private static string RelativeAndroid(string value)
    {
        const string prefix = "{UNITY_PATH}/Editor/Data/PlaybackEngines/AndroidPlayer/";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"不支持的模块解压路径：{value}");
        return value[prefix.Length..];
    }

    internal static void InstallAndroid(EditorInstall editor, IReadOnlyList<(Module Module, string Zip)> files,
        IProgress<(string Label, int Percent)> progress, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(editor.Android, "UnityEditor.Android.Extensions.dll")))
            throw new IOException("请先安装这个 Unity 版本的 Android Build Support，再安装 JDK、SDK 和 NDK。");
        foreach (var process in Process.GetProcessesByName("Unity"))
        using (process)
        {
            string? running = null;
            try { running = process.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            if (string.Equals(running, editor.Exe, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请关闭所选版本的 Unity 编辑器后再安装。");
        }
        var work = Path.Combine(editor.Root, $".unity-modules-{Guid.NewGuid():N}");
        var combined = Path.Combine(work, "combined");
        try
        {
            Directory.CreateDirectory(combined);
            foreach (var (m, zip) in files)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(($"解压 {m.Name}", -1));
                var extracted = Path.Combine(work, Guid.NewGuid().ToString("N"));
                var destination = Under(extracted, RelativeAndroid(m.Destination!));
                Directory.CreateDirectory(destination);
                using (var archive = ZipFile.OpenRead(zip))
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Under(destination, entry.FullName);
                    if (entry.Name.Length == 0) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: false);
                    }
                }
                if (m.ExtractedPathRename is { } rule)
                {
                    var source = Under(extracted, RelativeAndroid(rule.From));
                    var target = Under(extracted, RelativeAndroid(rule.To));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (string.Equals(Path.GetDirectoryName(source), target, StringComparison.OrdinalIgnoreCase))
                    {
                        // Unity's NDK rule flattens NDK/android-ndk-* into its parent.
                        var detached = Path.Combine(work, Guid.NewGuid().ToString("N"));
                        Directory.Move(source, detached);
                        Directory.Delete(target, recursive: false);
                        Directory.Move(detached, target);
                    }
                    else Directory.Move(source, target);
                }
                Merge(extracted, combined, ct);
            }
            progress.Report(("核对安装目录；不会覆盖不同的文件", -1));
            Merge(combined, editor.Android, ct);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    private static void Merge(string source, string destination, CancellationToken ct)
    {
        var moves = new List<(string Source, string Target, bool Directory)>();
        void Plan(string from, string to)
        {
            ct.ThrowIfCancellationRequested();
            if (Path.Exists(to) && (File.GetAttributes(to) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"目标包含链接，无法安全安装：{to}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(to, Path.GetFileName(entry));
                bool folder = Directory.Exists(entry);
                if (!Path.Exists(target)) moves.Add((entry, target, folder));
                else if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"目标包含链接，无法安全安装：{target}");
                else if (folder && Directory.Exists(target)) Plan(entry, target);
                else if (folder || !File.Exists(target) || !SameFile(entry, target))
                    throw new IOException($"已有文件与下载内容不同：{target}。请先备份并移走冲突的组件目录。");
            }
        }
        Plan(source, destination);
        ct.ThrowIfCancellationRequested();
        foreach (var (from, to, folder) in moves)
            if (folder) Directory.Move(from, to); else File.Move(from, to);
    }

    private static bool SameFile(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var x = File.OpenRead(a);
        using var y = File.OpenRead(b);
        return SHA256.HashData(x).AsSpan().SequenceEqual(SHA256.HashData(y));
    }
}
