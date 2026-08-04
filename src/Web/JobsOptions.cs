namespace Netsoft.Jobs.Web;

/// <summary>
/// Web ホストの設定。appsettings.json の <c>Jobs</c> セクションから束ねる。
/// </summary>
/// <remarks>
/// IOptions を使わず起動時に 1 度だけ読む。どの値も実行中に差し替えられない
/// （DB のパスも、エンジンを常駐させるかも、起動時に決まったら変えられない）ので、
/// 変更の再読込みという IOptionsMonitor の能力に払うものが無い。
/// </remarks>
public sealed class JobsOptions
{
    /// <summary>設定セクションの名前。</summary>
    public const string SectionName = "Jobs";

    /// <summary>
    /// DB ファイルのパス。相対パスはコンテンツルート基準で解決される。
    /// </summary>
    /// <remarks>
    /// カレントディレクトリ基準にしないのは、<c>dotnet run</c> をどこから叩いたかで
    /// 別の DB ができてしまうため。テストは一時ファイルへの絶対パスを渡して差し替える。
    /// </remarks>
    public string DatabasePath { get; set; } = "jobs.db";

    /// <summary>
    /// 実行エンジンを常駐させるか。
    /// </summary>
    /// <remarks>
    /// テストが false にして止める。エンジンが勝手に Job を進めると、
    /// 「待機中の Job をキャンセルできる」のような状態を前提にしたテストが不安定になる。
    /// </remarks>
    public bool RunExecutionEngine { get; set; } = true;
}
