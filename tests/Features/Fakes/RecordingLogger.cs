using Microsoft.Extensions.Logging;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 書かれたログを記録する <see cref="ILogger{T}"/>。文言に加えて、名前付きの値とスコープの中身を残す。
/// </summary>
/// <remarks>
/// 「JobId で絞れば Job の一生が読める」ことを保証するには、文言だけでなく
/// 構造化ログの名前付きパラメータ（<c>{JobId}</c> など）とスコープが残っていることを
/// 確かめる必要がある。文字列比較だけでは、名前が消えて文言に埋まっていても通ってしまう。
/// テストは逐次にログを書く前提なので、スレッド安全にはしていない。
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<RecordedLog> _entries = [];
    private readonly List<IReadOnlyDictionary<string, object?>> _activeScopes = [];

    /// <summary>記録されたログ。書かれた順。</summary>
    public IReadOnlyList<RecordedLog> Entries => _entries;

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        IReadOnlyDictionary<string, object?> values = ToValues(state);
        _activeScopes.Add(values);
        return new Scope(_activeScopes, values);
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        // スコープは書いた時点のものを写し取る。後からスコープが閉じても記録は変わらない。
        Dictionary<string, object?> scopeValues = [];
        foreach (IReadOnlyDictionary<string, object?> scope in _activeScopes)
        {
            foreach (KeyValuePair<string, object?> pair in scope)
            {
                scopeValues[pair.Key] = pair.Value;
            }
        }

        _entries.Add(new RecordedLog(logLevel, formatter(state, exception), ToValues(state), scopeValues));
    }

    /// <summary>
    /// state から名前付きの値を取り出す。
    /// </summary>
    /// <remarks>
    /// メッセージテンプレートの名前付きの値は KeyValuePair の列として渡ってくる
    /// （構造化コレクタが読むのと同じ形）。それ以外の state は名前を持たないので空として扱う。
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> ToValues<TState>(TState state)
    {
        Dictionary<string, object?> values = [];

        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (KeyValuePair<string, object?> pair in pairs)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return values;
    }

    /// <summary>
    /// <see cref="BeginScope{TState}"/> が返す handle。閉じたらスコープの列から自分を外す。
    /// </summary>
    private sealed class Scope : IDisposable
    {
        private readonly List<IReadOnlyDictionary<string, object?>> _scopes;
        private readonly IReadOnlyDictionary<string, object?> _values;

        public Scope(List<IReadOnlyDictionary<string, object?>> scopes, IReadOnlyDictionary<string, object?> values)
        {
            _scopes = scopes;
            _values = values;
        }

        public void Dispose() => _scopes.Remove(_values);
    }
}

/// <summary>
/// 記録された 1 行分のログ。
/// </summary>
/// <param name="Level">ログレベル。</param>
/// <param name="Message">整形後の文言。</param>
/// <param name="State">メッセージテンプレートの名前付きの値。</param>
/// <param name="Scope">書いた時点で積まれていたスコープの値。外側から順にマージ済み。</param>
public sealed record RecordedLog(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyDictionary<string, object?> Scope);
