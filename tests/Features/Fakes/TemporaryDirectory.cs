namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// テスト 1 件ごとに作って捨てる一時ディレクトリ。
/// </summary>
/// <remarks>
/// ファイルを扱うハンドラは本物のファイルシステムでしか確かめられない。
/// <see cref="TemporaryJobStore"/> と同じく、作る場所を 1 箇所にまとめて
/// <see cref="IDisposable"/> で消す形にしてある。
/// </remarks>
public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        // 並行するテストと衝突しないよう、インスタンスごとに分ける（docs/build.md）。
        FullPath = Path.Combine(Path.GetTempPath(), "netsoft-jobs-features-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(FullPath);
    }

    /// <summary>この一時ディレクトリの絶対パス。</summary>
    public string FullPath { get; }

    /// <summary>この一時ディレクトリからの相対パスを絶対パスにする。</summary>
    public string Combine(string relativePath) => Path.Combine(FullPath, relativePath);

    /// <summary>途中のディレクトリごと作り、内容を書き込む。作ったファイルの絶対パスを返す。</summary>
    public string WriteFile(string relativePath, string content)
    {
        string path = Combine(relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>途中のディレクトリごと作る。作ったディレクトリの絶対パスを返す。</summary>
    public string CreateSubdirectory(string relativePath)
    {
        string path = Combine(relativePath);

        Directory.CreateDirectory(path);

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えない（docs/build.md「テストの後始末」）。
        }
    }
}
