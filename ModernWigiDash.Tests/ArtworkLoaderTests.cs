using Windows.Foundation;
using Windows.Storage.Streams;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class ArtworkLoaderTests
{
    private sealed class FakeArtworkDecoder : IArtworkDecoder
    {
        public int DecodeCalls { get; private set; }

        public SKBitmap? Result { get; set; }

        public Func<Task<ArtworkDecodeResult>>? Handler { get; set; }

        public Task<ArtworkDecodeResult> DecodeAsync(IRandomAccessStreamReference thumbnail)
        {
            DecodeCalls++;
            return Handler is not null
                ? Handler()
                : Task.FromResult(new ArtworkDecodeResult(Result, false));
        }
    }

    private sealed class FakeThumbnail : IRandomAccessStreamReference
    {
        private readonly IRandomAccessStreamWithContentType? _stream;

        public FakeThumbnail(IRandomAccessStreamWithContentType? stream = null) => _stream = stream;

        public IAsyncOperation<IRandomAccessStreamWithContentType> OpenReadAsync()
            => _stream is null
                ? throw new NotImplementedException("Fake thumbnails are never opened through the decoder seam.")
                : new CompletedAsyncOperation<IRandomAccessStreamWithContentType>(_stream);
    }

    private sealed class CompletedAsyncOperation<T> : IAsyncOperation<T>
    {
        private readonly T _result;

        public CompletedAsyncOperation(T result) => _result = result;

        public AsyncStatus Status => AsyncStatus.Completed;

        public Exception ErrorCode => null!;

        public uint Id => 0;

        public AsyncOperationCompletedHandler<T> Completed { get => null!; set { /* no-op: completion is driven by the test's manual invocation */ } }

        public T GetResults() => _result;

        public void Close() { }

        public void Cancel() { }
    }

    private sealed class FakeBigStream : IRandomAccessStreamWithContentType
    {
        public ulong Size { get; set; } = 11UL * 1024 * 1024;

        public string ContentType => "image/png";

        public bool CanRead => throw new NotImplementedException();

        public bool CanWrite => throw new NotImplementedException();

        public ulong Position => throw new NotImplementedException();

        public IInputStream GetInputStreamAt(ulong position) => throw new NotImplementedException();

        public IOutputStream GetOutputStreamAt(ulong position) => throw new NotImplementedException();

        public void Seek(ulong position) => throw new NotImplementedException();

        public IRandomAccessStream CloneStream() => throw new NotImplementedException();

        public void Dispose() { }

        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
            => throw new NotImplementedException();

        public IAsyncOperation<bool> FlushAsync() => throw new NotImplementedException();

        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) => throw new NotImplementedException();
    }

    private static MediaSessionUpdate Update(string artKey, IRandomAccessStreamReference? thumbnail = null)
        => new(new MediaSnapshot { Title = artKey }, thumbnail, artKey);

    [TestMethod]
    public async Task NotifySnapshotChanged_SameKey_DoesNotReload()
    {
        var decoder = new FakeArtworkDecoder { Result = new SKBitmap(4, 4) };
        var loader = new ArtworkLoader(decoder);

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail()));
        await TestWait.WaitUntilAsync(() => loader.Current.ArtKey == "keyA", TimeSpan.FromSeconds(5));

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail()));

        Assert.AreEqual(1, decoder.DecodeCalls);
    }

    [TestMethod]
    public async Task NotifySnapshotChanged_ChangedKey_ReloadsAndRaisesArtworkChangedWithNewArtworkAndKey()
    {
        var bitmapA = new SKBitmap(2, 2);
        var bitmapB = new SKBitmap(3, 3);
        var decoder = new FakeArtworkDecoder { Result = bitmapA };
        var loader = new ArtworkLoader(decoder);
        var events = new List<ArtworkLoaded?>();
        loader.ArtworkChanged += e => events.Add(e);

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail()));
        await TestWait.WaitUntilAsync(() => loader.Current.Bitmap is not null, TimeSpan.FromSeconds(5));

        decoder.Result = bitmapB;
        loader.NotifySnapshotChanged(Update("keyB", new FakeThumbnail()));
        await TestWait.WaitUntilAsync(() => events.Any(e => e?.ArtKey == "keyB"), TimeSpan.FromSeconds(5));

        Assert.IsTrue(ReferenceEquals(loader.Current.Bitmap, bitmapB));
        Assert.AreEqual("keyB", loader.Current.ArtKey);
        Assert.IsTrue(events.Any(e => e?.ArtKey == "keyB" && ReferenceEquals(e.Bitmap, bitmapB)));
    }

    [TestMethod]
    public async Task NotifySnapshotChanged_ArtworkBecomesAvailableForSameKey_Reloads()
    {
        var decoder = new FakeArtworkDecoder { Result = new SKBitmap(4, 4) };
        var loader = new ArtworkLoader(decoder);

        loader.NotifySnapshotChanged(Update("keyA"));
        Assert.AreEqual(0, decoder.DecodeCalls);

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail()));
        await TestWait.WaitUntilAsync(() => loader.Current.ArtKey == "keyA", TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, decoder.DecodeCalls);
        Assert.IsNotNull(loader.Current.Bitmap);
    }

    [TestMethod]
    public async Task Load_ThumbnailStreamExceedsTenMegabytes_NoArtworkNoCrashLogged()
    {
        var errors = new List<string>();
        var loader = new ArtworkLoader(new WinRtArtworkDecoder(), (message, ex) => errors.Add(message));

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail(new FakeBigStream())));
        await TestWait.WaitUntilAsync(() => errors.Count > 0, TimeSpan.FromSeconds(5));

        Assert.IsNull(loader.Current.Bitmap);
        Assert.AreEqual(1, errors.Count);
        Assert.IsTrue(errors[0].Contains("10 MB"), $"log was '{errors[0]}'");
    }

    [TestMethod]
    public async Task Load_SlowLoadCompletingAfterNewerKeyChange_DoesNotPublish()
    {
        var tcsA = new TaskCompletionSource<ArtworkDecodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsB = new TaskCompletionSource<ArtworkDecodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bitmapA = new SKBitmap(2, 2);
        var bitmapB = new SKBitmap(3, 3);
        int calls = 0;
        var decoder = new FakeArtworkDecoder { Handler = () => ++calls == 1 ? tcsA.Task : tcsB.Task };
        var loader = new ArtworkLoader(decoder);
        var events = new List<ArtworkLoaded?>();
        loader.ArtworkChanged += e => events.Add(e);

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail()));
        loader.NotifySnapshotChanged(Update("keyB", new FakeThumbnail()));

        tcsB.SetResult(new ArtworkDecodeResult(bitmapB, false));
        await TestWait.WaitUntilAsync(() => ReferenceEquals(loader.Current.Bitmap, bitmapB), TimeSpan.FromSeconds(5));

        tcsA.SetResult(new ArtworkDecodeResult(bitmapA, false));
        await TestWait.WaitUntilAsync(() => events.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.IsTrue(ReferenceEquals(loader.Current.Bitmap, bitmapB));
        Assert.IsFalse(events.Any(e => ReferenceEquals(e?.Bitmap, bitmapA)));
    }

    [TestMethod]
    public async Task DisposeAll_DrainsRetirementAndIsSafeTwice()
    {
        var decoder = new FakeArtworkDecoder { Result = new SKBitmap(4, 4) };
        var loader = new ArtworkLoader(decoder);

        loader.NotifySnapshotChanged(Update("keyA", new FakeThumbnail()));
        await TestWait.WaitUntilAsync(() => loader.Current.Bitmap is not null, TimeSpan.FromSeconds(5));

        loader.DisposeAll();

        Assert.AreEqual(0, loader.RetirementSet.PendingCount);

        loader.DisposeAll();

        Assert.AreEqual(0, loader.RetirementSet.PendingCount);
    }
}
