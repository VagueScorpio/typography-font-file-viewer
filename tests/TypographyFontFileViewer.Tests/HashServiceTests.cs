using System.IO;
using TypographyFontFileViewer.Services;
using Xunit;

namespace TypographyFontFileViewer.Tests;

/// <summary>Content hashing: the identity a favourite is recognised by.</summary>
public class HashServiceTests : TempFolderTest
{
    public HashServiceTests() : base("hash") { }

    private string Write(string name, string contents)
    {
        string path = Path.Combine(Root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task TheSameContentsHashTheSame_WhateverTheNames()
    {
        string first = Write("Roboto.ttf", "identical bytes");
        string second = Write("SomethingElse.ttf", "identical bytes");

        var hashes = new HashService();

        // This is the whole point: identity is the contents, not the name.
        Assert.Equal(
            await hashes.ComputeAsync(first, TestCancellation),
            await hashes.ComputeAsync(second, TestCancellation));
    }

    [Fact]
    public async Task DifferentContents_HashDifferently()
    {
        string first = Write("A.ttf", "one");
        string second = Write("B.ttf", "two");

        var hashes = new HashService();

        Assert.NotEqual(
            await hashes.ComputeAsync(first, TestCancellation),
            await hashes.ComputeAsync(second, TestCancellation));
    }

    [Fact]
    public async Task AFileThatChanges_IsHashedAgain()
    {
        string path = Write("Changing.ttf", "before");

        var hashes = new HashService();
        string? before = await hashes.ComputeAsync(path, TestCancellation);

        // A new length and write time make a new cache key.
        await Task.Delay(20, TestCancellation);
        File.WriteAllText(path, "after, and longer");

        Assert.NotEqual(before, await hashes.ComputeAsync(path, TestCancellation));
    }

    [Fact]
    public async Task AMissingFile_IsNullRatherThanAnException()
    {
        var hashes = new HashService();
        Assert.Null(await hashes.ComputeAsync(
            Path.Combine(Root, "nowhere.ttf"), TestCancellation));
    }

    [Fact]
    public async Task HashingDoesNotLockTheFile()
    {
        string path = Write("Open.ttf", "contents");

        var hashes = new HashService();
        Assert.NotNull(await hashes.ComputeAsync(path, TestCancellation));

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
