using System.Globalization;
using System.IO.Compression;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 指定されたディレクトリを ZIP に固める Job。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DemoJobHandler"/> に続く 2 つ目のハンドラで、こちらは実物の仕事をする。
/// ファイル数だけ時間がかかり、途中で止めたくなる処理なので、
/// 長時間実行 Job の基盤が実際の仕事で成り立つかをこれで確かめる。
/// </para>
/// <para>
/// 受け取るパラメータは対象ディレクトリのパス 1 つだけにしてある。出力先も受け取ると
/// 画面の入力欄が 2 つ必要になり、「<see cref="Domain.Job.Parameters"/> は
/// ハンドラだけが解釈する不透明な文字列 1 本」という基盤の前提と噛み合わなくなる。
/// 出力先は規則で決める方を選んだ。
/// </para>
/// </remarks>
public sealed class ArchiveJobHandler : IJobHandler
{
    /// <summary>この Job の種類。登録時の JobType にこの値を指定する。</summary>
    public const string ArchiveJobType = "archive";

    /// <summary>出力する zip の名前に埋め込む日時の書式。</summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    private readonly TimeProvider _timeProvider;

    public ArchiveJobHandler(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public string JobType => ArchiveJobType;

    /// <summary>
    /// <paramref name="parameters"/> を対象ディレクトリのパスとして解釈し、
    /// 同じ階層に <c>&lt;ディレクトリ名&gt;-yyyyMMdd-HHmmss.zip</c> を作る。
    /// </summary>
    /// <remarks>
    /// 名前に日時を入れるのは、同じディレクトリを 2 回固めたときに
    /// 先に作った zip を黙って壊さないため。
    /// </remarks>
    /// <exception cref="ArgumentException">パスが空、またはドライブ直下の場合。</exception>
    /// <exception cref="DirectoryNotFoundException">対象ディレクトリが存在しない場合。</exception>
    /// <exception cref="IOException">読み取れないファイルがある、または zip を作れない場合。</exception>
    /// <exception cref="OperationCanceledException">キャンセルされた場合。</exception>
    public async Task ExecuteAsync(JobId jobId, string parameters, CancellationToken cancellationToken)
    {
        string sourceDirectory = ResolveSourceDirectory(parameters);
        string destinationPath = BuildDestinationPath(sourceDirectory);

        await CreateArchiveAsync(sourceDirectory, destinationPath, cancellationToken);
    }

    private static string ResolveSourceDirectory(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            throw new ArgumentException(
                "固める対象のディレクトリのパスを指定してください。",
                nameof(parameters));
        }

        // 相対パスは Job を実行しているプロセスの作業ディレクトリ次第で別の場所を指す。
        // 何を固めたのかを後から追えるように、ここで絶対パスへ寄せてしまう。
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parameters.Trim()));

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"対象のディレクトリが見つかりません: \"{fullPath}\"。");
        }

        return fullPath;
    }

    private string BuildDestinationPath(string sourceDirectory)
    {
        // ドライブ直下には「同じ階層」が無く、出力先を置く場所が決まらない。
        // 通り過ぎると Path.Combine が意図しない場所を指すので、ここで断る。
        string? parent = Path.GetDirectoryName(sourceDirectory);
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException(
                $"ドライブ直下は対象にできません: \"{sourceDirectory}\"。個別のディレクトリを指定してください。",
                nameof(sourceDirectory));
        }

        // 時刻は UTC で取る。ローカル時刻にすると出力名が実行環境の時間帯に左右され、
        // テストと運用で違う名前になる。
        string timestamp = _timeProvider.GetUtcNow().ToString(TimestampFormat, CultureInfo.InvariantCulture);

        return Path.Combine(parent, $"{Path.GetFileName(sourceDirectory)}-{timestamp}.zip");
    }

    private static async Task CreateArchiveAsync(
        string sourceDirectory,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        // CreateNew にして既存の zip を上書きしない。上書きにすると、この後で失敗したときの
        // 後始末（書きかけの削除）が、他の誰かが置いた完成品を消すことになりうる。
        // ここで失敗した場合は zip をまだ作っていないので、後始末の対象にもしない。
        FileStream destination = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        try
        {
            await using (destination)
            using (ZipArchive archive = new(destination, ZipArchiveMode.Create))
            {
                await WriteEntriesAsync(archive, sourceDirectory, cancellationToken);
            }
        }
        catch
        {
            // 中断・失敗したら書きかけの zip を消す。残すと完成した書庫と見分けが付かず、
            // 中身が欠けたものを使われてしまう。
            DeleteIncompleteArchive(destinationPath);
            throw;
        }
    }

    private static async Task WriteEntriesAsync(
        ZipArchive archive,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        // ZipFile.CreateFromDirectory は一発で固めてしまい、途中で止められない。
        // 協調的キャンセルは基盤の約束なので、エントリを 1 つずつ書いて毎回要求を確認する。
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,

            // 既定は true で、読めないものを黙って飛ばす。飛ばすと中身の欠けた zip が
            // 完成品として残るので、気づける形（例外）にする。
            IgnoreInaccessible = false,

            // 既定では隠しファイル・システムファイルが除かれる。これも黙って欠ける原因になる。
            AttributesToSkip = FileAttributes.None,
        };

        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ZipArchiveEntry entry = archive.CreateEntry(
                ToEntryName(sourceDirectory, file),
                CompressionLevel.Optimal);

            await using Stream entryStream = entry.Open();
            await using FileStream content = OpenForRead(file);

            await content.CopyToAsync(entryStream, cancellationToken);
        }

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 空のディレクトリはファイルの一覧に現れない。エントリを足さないと
            // 展開したときに消えてしまうので、空のものだけ末尾に "/" を付けて入れる。
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                continue;
            }

            archive.CreateEntry(ToEntryName(sourceDirectory, directory) + "/");
        }
    }

    /// <remarks>
    /// zip の中の区切りは "/" と決まっている。Windows の "\" のまま入れると、
    /// 他のツールで展開したときに階層ではなく名前の一部として扱われる。
    /// </remarks>
    private static string ToEntryName(string sourceDirectory, string path) =>
        Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/');

    private static FileStream OpenForRead(string path)
    {
        try
        {
            // 他のプロセスが開いていても読めるように共有を広く取る。それでも読めないもの
            //（権限が無い、リンク先が無い等）は飛ばさずに失敗させる。1 つ欠けた zip を
            // 完成品として渡すより、何が読めなかったかを利用者に伝えてやり直させる方がよい。
            // 途中経過を伝える口が無く（IJobHandler は例外でしか失敗を伝えられない）、
            // 飛ばすと欠けたことが誰にも残らないため。
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"読み取れないファイルがあるため中断しました: \"{path}\"。{ex.Message}",
                ex);
        }
    }

    private static void DeleteIncompleteArchive(string destinationPath)
    {
        try
        {
            File.Delete(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後始末の失敗で Job の結末を変えない。ここで例外を投げ直すと、
            // 本当の失敗理由（キャンセルや読み取り失敗）が消えて利用者に届かなくなる。
        }
    }
}
