using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Cpfs.Core.Text;

// CPFS's own sentence, named by its resource key, with its arguments: the app shows it in the UI language and logs it
// in English. An argument that is a TextKey is a sentence or word of its own, resolved the same way; a string is a
// value (a path, a name, Windows' own words) and keeps its own direction in right-to-left text.
public sealed class TextKey
{
    public TextKey(string key, params object?[] args)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Args = args ?? Array.Empty<object?>();
    }

    public string Key { get; }

    public IReadOnlyList<object?> Args { get; }

    // Set by the app at start: the English text, for exception messages and the log. Unset (tests), the key and its
    // arguments.
    public static Func<TextKey, string>? English { get; set; }

    public string ToEnglish() => English?.Invoke(this) ?? KeyForm();

    public override string ToString() => ToEnglish();

    private string KeyForm() => Args.Count == 0
        ? Key
        : Key + "(" + string.Join(", ", Args.Select(a => a is TextKey t ? t.ToEnglish() : Convert.ToString(a, CultureInfo.InvariantCulture))) + ")";
}

// An exception that may carry CPFS's own sentence; null when its message is Windows' own words.
public interface IHasText
{
    TextKey? Text { get; }
}

// A failure described in CPFS's own words. Its message is the English text, for the log; the screen shows Text. An
// InvalidOperationException, so the filters that expect one (State.IsExpected) still do.
public sealed class CpfsException : InvalidOperationException, IHasText
{
    public CpfsException(string key, params object?[] args)
        : this(new TextKey(key, args), null)
    {
    }

    public CpfsException(Exception inner, string key, params object?[] args)
        : this(new TextKey(key, args), inner)
    {
    }

    private CpfsException(TextKey text, Exception? inner)
        : base(text.ToEnglish(), inner)
    {
        Text = text;
    }

    public TextKey? Text { get; }
}
