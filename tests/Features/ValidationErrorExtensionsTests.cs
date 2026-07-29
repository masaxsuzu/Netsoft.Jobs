namespace Netsoft.Jobs.Features.Tests;

public sealed class ValidationErrorExtensionsTests
{
    [Fact]
    public void 項目名をキーにしてメッセージがまとめられる()
    {
        // エンドポイントはこの形をそのまま 400 の本文にする。薄さを保つ代わりにここで押さえる。
        IReadOnlyList<ValidationError> errors =
        [
            new ValidationError("name", "名前を入力してください。"),
            new ValidationError("jobType", "Job の種類を入力してください。"),
            new ValidationError("name", "名前が長すぎます。"),
        ];

        IDictionary<string, string[]> converted = errors.ToProblemDetailsErrors();

        Assert.Equal(["jobType", "name"], converted.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["名前を入力してください。", "名前が長すぎます。"], converted["name"]);
        Assert.Equal(["Job の種類を入力してください。"], converted["jobType"]);
    }
}
