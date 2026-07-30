using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PakAssetStudio.Services;

public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// 多语言服务：从程序目录下的 Languages/*.json 加载语言文件。
/// 缺失条目回退到简体中文，完全没有时显示键名本身。
/// 运行时切换语言后通过索引器通知所有 {l:Loc} 绑定刷新。
/// 用户可以向 Languages 目录投放自己的语言文件扩展翻译。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private const string FallbackCode = "zh-CN";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _folder;
    private readonly bool _persist;
    private readonly List<LanguageInfo> _languages = [];
    private readonly Dictionary<string, string> _languagePaths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>语言切换后触发，供代码刷新动态生成的文本。</summary>
    public event EventHandler? LanguageChanged;

    public string CurrentCode { get; private set; } = FallbackCode;
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _languages;

    public string this[string key] => Get(key);

    private LocalizationService()
    {
        _folder = LanguagesFolder;
        _persist = true;
    }

    /// <summary>供测试使用：从指定目录加载语言文件，不读写用户偏好。</summary>
    public LocalizationService(string languagesFolder)
    {
        _folder = languagesFolder;
    }

    public static string LanguagesFolder => Path.Combine(AppContext.BaseDirectory, "Languages");

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        DiscoverLanguages();
        var preferred = _persist ? LoadPreference() : null;
        preferred ??= CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : FallbackCode;
        SetLanguage(_languages.Any(language => language.Code == preferred)
            ? preferred
            : _languages.FirstOrDefault()?.Code ?? FallbackCode);
    }

    public string Get(string key) =>
        _strings.TryGetValue(key, out var value) ? value : key;

    public string Format(string key, params object[] args)
    {
        var format = Get(key);
        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            // 第三方语言文件的占位符损坏时显示原文，不让界面操作崩溃。
            return format;
        }
    }

    public static string Text(string key) => Instance.Get(key);

    public static string TextFormat(string key, params object[] args) => Instance.Format(key, args);

    public void SetLanguage(string code)
    {
        var selected = _languages.FirstOrDefault(language =>
            language.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        var safeCode = selected?.Code ?? FallbackCode;
        var fallbackPath = _languagePaths.GetValueOrDefault(
            FallbackCode, Path.Combine(_folder, FallbackCode + ".json"));
        var merged = ReadLanguageFile(fallbackPath);
        if (!safeCode.Equals(FallbackCode, StringComparison.OrdinalIgnoreCase))
        {
            var selectedPath = _languagePaths.GetValueOrDefault(
                safeCode, Path.Combine(_folder, safeCode + ".json"));
            foreach (var pair in ReadLanguageFile(selectedPath))
                merged[pair.Key] = pair.Value;
        }

        _strings = merged;
        CurrentCode = safeCode;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        if (_persist) SavePreference();
    }

    public static Dictionary<string, string> ReadLanguageFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            var model = JsonSerializer.Deserialize<LanguageFileModel>(File.ReadAllText(path), JsonOptions);
            if (model?.Strings is null) return result;
            foreach (var pair in model.Strings)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                    result[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 语言文件损坏、被占用或不可访问时忽略，保持回退文本。
        }
        return result;
    }

    private void DiscoverLanguages()
    {
        _languages.Clear();
        _languagePaths.Clear();
        try
        {
            if (Directory.Exists(_folder))
            {
                foreach (var file in Directory.EnumerateFiles(_folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var model = JsonSerializer.Deserialize<LanguageFileModel>(File.ReadAllText(file), JsonOptions);
                        var code = string.IsNullOrWhiteSpace(model?.Code)
                            ? Path.GetFileNameWithoutExtension(file)
                            : model!.Code!;
                        if (!IsSafeLanguageCode(code) || _languages.Any(language =>
                                language.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var name = string.IsNullOrWhiteSpace(model?.Language) ? code : model!.Language!;
                        _languages.Add(new LanguageInfo(code, name));
                        _languagePaths[code] = file;
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                    {
                        // 跳过无法解析或读取的语言文件。
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Languages 目录本身不可访问时使用内置回退项。
        }

        if (_languages.Count == 0)
            _languages.Add(new LanguageInfo(FallbackCode, "简体中文"));
    }

    private static bool IsSafeLanguageCode(string code) =>
        code.Length is > 0 and <= 32 && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PakAssetStudio", "settings.json");

    private string? LoadPreference()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return null;
            var model = JsonSerializer.Deserialize<PreferenceModel>(File.ReadAllText(PreferencePath), JsonOptions);
            return model?.Language;
        }
        catch
        {
            return null;
        }
    }

    private void SavePreference()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, JsonSerializer.Serialize(new PreferenceModel { Language = CurrentCode }));
        }
        catch
        {
            // 偏好写入失败不影响使用。
        }
    }

    private sealed class LanguageFileModel
    {
        public string? Code { get; set; }
        public string? Language { get; set; }
        public Dictionary<string, string>? Strings { get; set; }
    }

    private sealed class PreferenceModel
    {
        public string? Language { get; set; }
    }
}
