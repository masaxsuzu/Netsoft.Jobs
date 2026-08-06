namespace Netsoft.Jobs.Chaos.Tests;

/// <summary>
/// DB が使えない状態で起動したとき、黙って動き続けないこと。
/// </summary>
/// <remarks>
/// <para>
/// この基盤は状態を全部 DB に置いている。書けない DB で起動が通ってしまうと、
/// 画面は開くのに登録が毎回失敗する（あるいは Job が消える）状態になり、
/// 利用者は「動いているのに何かおかしい」に付き合うことになる。
/// <b>使えないなら起動の時点で落ちる</b>のが正しい。
/// </para>
/// <para>
/// 権限を落とす形（chmod）では確かめない。root で走らせると権限が素通りするので、
/// 環境によって結果が変わるテストになる。ここでは「そもそも開けないパス」で表す。
/// </para>
/// </remarks>
public sealed class UnusableDatabaseTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netsoft-jobs-chaos", Path.GetRandomFileName());

    public UnusableDatabaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えない（docs/build.md「テストの後始末」）。
        }
    }

    /// <summary>
    /// DB のパスがディレクトリなら、起動の時点で落ちる。
    /// </summary>
    /// <remarks>
    /// スキーマの用意は <c>app.RunAsync()</c> より前にあるので、ここで落ちれば
    /// HTTP の口は 1 度も開かない。開いてしまうと、上に書いた「動いているのにおかしい」
    /// 状態になる。
    /// </remarks>
    [Fact]
    public async Task DBを開けないなら起動の時点で落ちる()
    {
        string path = Path.Combine(_directory, "jobs.db");
        Directory.CreateDirectory(path);

        (int exitCode, string output) = await JobsHost.RunUntilExitAsync(path, Limit);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SqliteException", output);
    }

    /// <summary>
    /// 置き場所ごと無いなら、作りにいかずに落ちる。
    /// </summary>
    /// <remarks>
    /// 親ディレクトリを勝手に作ると、設定を打ち間違えたときに気づかないまま
    /// 空の DB で走り出す（前回までの Job が全部消えたように見える）。
    /// </remarks>
    [Fact]
    public async Task 親ディレクトリが無いなら作らずに落ちる()
    {
        string path = Path.Combine(_directory, "no-such-directory", "jobs.db");

        (int exitCode, string output) = await JobsHost.RunUntilExitAsync(path, Limit);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("SqliteException", output);
        Assert.False(File.Exists(path));
    }
}
