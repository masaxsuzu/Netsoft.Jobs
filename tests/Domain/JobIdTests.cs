namespace Netsoft.Jobs.Domain.Tests;

public sealed class JobIdTests
{
    [Fact]
    public void 文字列から識別子を作れる()
    {
        JobId id = JobId.From("job-1");

        Assert.Equal("job-1", id.Value);
        Assert.Equal("job-1", id.ToString());
        Assert.False(id.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void 空文字や空白だけの識別子は作れない(string value)
    {
        Assert.Throws<ArgumentException>(() => JobId.From(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryFromは不正な値でも例外を投げない(string? value)
    {
        Assert.False(JobId.TryFrom(value, out JobId id));
        Assert.True(id.IsEmpty);
    }

    [Fact]
    public void TryFromは有効な値なら識別子を返す()
    {
        Assert.True(JobId.TryFrom("job-1", out JobId id));
        Assert.Equal("job-1", id.Value);
    }

    [Fact]
    public void 同じ文字列の識別子は等しい()
    {
        Assert.Equal(JobId.From("job-1"), JobId.From("job-1"));
        Assert.NotEqual(JobId.From("job-1"), JobId.From("job-2"));
    }

    [Fact]
    public void 未初期化の識別子は空として扱える()
    {
        JobId id = default;

        Assert.True(id.IsEmpty);
        Assert.Equal(string.Empty, id.Value);
    }
}
