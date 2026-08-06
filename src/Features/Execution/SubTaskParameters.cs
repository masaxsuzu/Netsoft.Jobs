using System.Globalization;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// subtasks Job のパラメータ「個数 秒数」の解釈。実行（ハンドラ）と編集（検証）が同じ定義を使う。
/// </summary>
/// <remarks>
/// 何が有効かの定義は <see cref="TryParse"/> が持ち、<see cref="Parse"/> は委譲する
/// （<see cref="Domain.JobId"/> の From / TryFrom と同じ構図）。
/// 既定値は置かない。書き損じを既定値で流すと、意図と違う長さで走っていることに
/// 利用者が気づけない。
/// </remarks>
public static class SubTaskParameters
{
    /// <summary>受け付ける形を利用者へ案内する文。</summary>
    /// <remarks>
    /// 受理条件を持つのは <see cref="TryParse"/> だけなので、それを説明する文もここに置く。
    /// 写しを別の場所（編集 API の検証など）に置くと、条件を変えたときに片方だけが嘘になる。
    /// </remarks>
    public const string Expectation = "「個数 秒数」を空白区切りの正の整数で指定してください（例: 3 5）。";

    /// <summary>解釈する。読めなければ例外。実行の入口（ハンドラ）用。</summary>
    /// <exception cref="FormatException">「個数 秒数」として読めない場合。</exception>
    public static (int Count, int Waits) Parse(string parameters) =>
        TryParse(parameters, out int count, out int waits)
            ? (count, waits)
            : throw new FormatException(
                $"サブタスクの指定として解釈できません: \"{parameters}\"。" + Expectation);

    /// <summary>例外を投げずに解釈する。外部入力（編集 API）の検証用。</summary>
    public static bool TryParse(string parameters, out int count, out int waits)
    {
        count = 0;
        waits = 0;

        string[] parts = parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out count)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out waits)
            && count >= 1
            && waits >= 1;
    }
}
