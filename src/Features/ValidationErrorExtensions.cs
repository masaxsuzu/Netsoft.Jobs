namespace Netsoft.Jobs.Features;

/// <summary>
/// <see cref="ValidationError"/> を HTTP の表現へ変換する。
/// </summary>
public static class ValidationErrorExtensions
{
    /// <summary>
    /// 項目名をキーにしたメッセージの辞書へ変換する。
    /// </summary>
    /// <remarks>
    /// この形は <c>Results.ValidationProblem</c> がそのまま受け取れる（RFC 9457 の errors）。
    /// 同じ項目に複数のエラーが付きうるので、キーごとに配列でまとめる。
    /// </remarks>
    public static IDictionary<string, string[]> ToProblemDetailsErrors(this IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return errors
            .GroupBy(error => error.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).ToArray(),
                StringComparer.Ordinal);
    }
}
