using System.Globalization;
using System.Resources;
using System.Windows.Markup;

// Strings.resx (the neutral resources) is written in Spanish, so the ResourceManager must not
// probe for an "es" satellite assembly before falling back to it.
[assembly: NeutralResourcesLanguage("es")]

namespace Quill.Windows.Resources;

/// <summary>
/// Access to the UI copy in Resources/Strings.resx. All new UI text goes through here (C#) or
/// through <see cref="StrExtension"/> (XAML). Keys follow <c>Area_Name</c>.
/// </summary>
internal static class Loc
{
    private static readonly ResourceManager Manager =
        new("Quill.Windows.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>Culture override for tests or a future language setting; null means the
    /// current UI culture.</summary>
    public static CultureInfo? Culture { get; set; }

    /// <summary>Returns the localized string for <paramref name="key"/>, or the key in brackets
    /// when it is missing so a typo shows up in the UI instead of an empty label.</summary>
    public static string Get(string key) =>
        Manager.GetString(key, Culture) ?? $"[{key}]";

    /// <summary>Formats a composite resource such as <c>Speaker_Numbered</c> ("Interlocutor {0}").</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(Culture ?? CultureInfo.CurrentUICulture, Get(key), args);
}

/// <summary>
/// XAML access to Strings.resx: <c>Text="{loc:Str Summary_Decisions}"</c> with
/// <c>xmlns:loc="clr-namespace:Quill.Windows.Resources"</c>. Values resolve once at load
/// time; switching language at runtime is out of scope.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension : MarkupExtension
{
    public StrExtension()
    {
    }

    public StrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
