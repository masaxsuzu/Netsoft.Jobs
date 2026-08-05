using System.IO.Compression;

using Netsoft.Jobs.Domain;

using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// ファイルを扱うハンドラなので、本物のファイルシステム上で確かめる。
/// 時刻だけは固定して、出力名がテストの実行時刻に左右されないようにする。
/// </summary>
public sealed class ArchiveJobHandlerTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 3, 1, 2, 3, TimeSpan.Zero);

    private readonly TemporaryDirectory _root = new();
    private readonly ArchiveJobHandler _handler = new(new FixedTimeProvider(FixedNow));
    private readonly string _source;

    public ArchiveJobHandlerTests() => _source = _root.CreateSubdirectory("data");

    public void Dispose() => _root.Dispose();

    [Fact]
    public void 対応するJobTypeはarchive()
    {
        Assert.Equal("archive", _handler.JobType);
    }

    [Fact]
    public async Task 入れ子のファイルまで中身ごとzipに入る()
    {
        _root.WriteFile("data/top.txt", "てっぺん");
        _root.WriteFile("data/inner/nested.txt", "入れ子");

        await _handler.ExecuteAsync(JobId.From("job-1"), _source, CancellationToken.None);

        IReadOnlyDictionary<string, string> entries = ReadEntries(SingleArchive());

        Assert.Equal(2, entries.Count);
        Assert.Equal("てっぺん", entries["top.txt"]);

        // 区切りは "\" ではなく "/"。そうでないと他のツールで階層として展開されない。
        Assert.Equal("入れ子", entries["inner/nested.txt"]);
    }

    [Fact]
    public async Task 空のディレクトリもzipに残る()
    {
        _root.WriteFile("data/top.txt", "てっぺん");
        _root.CreateSubdirectory("data/empty");

        await _handler.ExecuteAsync(JobId.From("job-1"), _source, CancellationToken.None);

        // 空のディレクトリはファイルの一覧に現れないので、明示的に入れないと展開時に消える。
        Assert.Contains("empty/", ReadEntries(SingleArchive()).Keys);
    }

    [Fact]
    public async Task 隠しファイルもzipに入る()
    {
        // 既定の列挙は隠しファイルを飛ばす。飛ばすと中身の欠けた zip が完成品として残る。
        _root.WriteFile("data/.hidden", "隠し");

        await _handler.ExecuteAsync(JobId.From("job-1"), _source, CancellationToken.None);

        Assert.Equal("隠し", ReadEntries(SingleArchive())[".hidden"]);
    }

    [Fact]
    public async Task 出力先は対象と同じ階層でディレクトリ名と日時が入る()
    {
        _root.WriteFile("data/top.txt", "てっぺん");

        await _handler.ExecuteAsync(JobId.From("job-1"), _source, CancellationToken.None);

        // 日時が入るので、同じディレクトリを 2 回固めても先に作った zip を壊さない。
        Assert.Equal(_root.Combine("data-20260803-010203.zip"), SingleArchive());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task パスが空なら何を固めるか分かる例外になる(string parameters)
    {
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), parameters, CancellationToken.None));

        Assert.Contains("ディレクトリのパスを指定してください", error.Message);
    }

    [Fact]
    public async Task 存在しないディレクトリはそのパスが読める例外になる()
    {
        string missing = _root.Combine("missing");

        DirectoryNotFoundException error = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), missing, CancellationToken.None));

        Assert.Contains(missing, error.Message);
    }

    [Fact]
    public async Task ドライブ直下は出力先が決まらないので例外になる()
    {
        string driveRoot = Path.GetPathRoot(Path.GetTempPath())!;

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), driveRoot, CancellationToken.None));

        Assert.Contains("ドライブ直下", error.Message);
    }

    [Fact]
    public async Task キャンセルされると中断して書きかけのzipを残さない()
    {
        _root.WriteFile("data/top.txt", "てっぺん");

        // 実時間に依存させないため、最初からキャンセル済みのトークンで確かめる。
        // zip のファイル自体は書き始める前に作られるので、後始末は実際に走る。
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), _source, cancellation.Token));

        // 残すと中身の欠けたものが完成した書庫と見分けが付かない。
        Assert.Empty(Directory.GetFiles(_root.FullPath, "*.zip"));
    }

    [Fact]
    public async Task 読み取れないファイルがあると飛ばさず中断する()
    {
        _root.WriteFile("data/readable.txt", "読める");
        string unreadable = _root.WriteFile("data/unreadable.txt", "読めない");

        // 他のプロセスが排他で握っている状態を作る。
        using FileStream holder = new(unreadable, FileMode.Open, FileAccess.Read, FileShare.None);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), _source, CancellationToken.None));

        // 飛ばして続けると欠けたことが誰にも残らない。どれが読めなかったかを伝えて中断する。
        Assert.Contains("読み取れないファイルがあるため中断しました", error.Message);
        Assert.Contains(unreadable, error.Message);
        Assert.Empty(Directory.GetFiles(_root.FullPath, "*.zip"));
    }

    [Fact]
    public async Task 同じ名前のzipが既にあるときは上書きしない()
    {
        _root.WriteFile("data/top.txt", "てっぺん");
        string existing = _root.WriteFile("data-20260803-010203.zip", "先にあった書庫");

        await Assert.ThrowsAsync<IOException>(
            () => _handler.ExecuteAsync(JobId.From("job-1"), _source, CancellationToken.None));

        // 失敗したときの後始末が、他の誰かが置いた完成品を消してしまわないこと。
        Assert.Equal("先にあった書庫", File.ReadAllText(existing));
    }

    private string SingleArchive() => Assert.Single(Directory.GetFiles(_root.FullPath, "*.zip"));

    private static IReadOnlyDictionary<string, string> ReadEntries(string archivePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);

        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using StreamReader reader = new(entry.Open());
                return reader.ReadToEnd();
            });
    }
}
