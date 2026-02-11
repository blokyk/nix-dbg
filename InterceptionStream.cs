// Code from Mike Hadlow's 2015 "How to Record What Gets Written to or Read From a Stream"
// https://mikehadlow.blogspot.com/2015/06/c-how-to-record-what-gets-written-to-or.html
//
// Modified to add immediate flushing

public class InterceptionStream : Stream
{
    public Stream InnerStream { get; private set; }
    public Stream CopyStream { get; private set; }

    public InterceptionStream(Stream innerStream, Stream copyStream) {
        ArgumentNullException.ThrowIfNull(innerStream);
        ArgumentNullException.ThrowIfNull(copyStream);

        if (!copyStream.CanWrite) {
            throw new ArgumentException("copyStream is not writable");
        }

        InnerStream = innerStream;
        CopyStream = copyStream;
    }

    public override void Flush() => InnerStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => InnerStream.Seek(offset, origin);

    public override void SetLength(long value) => InnerStream.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count) {
        var bytesRead = InnerStream.Read(buffer, offset, count);

        if (bytesRead != 0) {
            CopyStream.Write(buffer, offset, bytesRead);
            CopyStream.Flush();
        }
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count) {
        InnerStream.Write(buffer, offset, count);
        CopyStream.Write(buffer, offset, count);
        CopyStream.Flush();
    }

    public override bool CanRead => InnerStream.CanRead;

    public override bool CanSeek => InnerStream.CanSeek;

    public override bool CanWrite => InnerStream.CanWrite;

    public override long Length => InnerStream.Length;

    public override long Position { get => InnerStream.Position; set => InnerStream.Position = value; }

    protected override void Dispose(bool disposing) => InnerStream.Dispose();
}