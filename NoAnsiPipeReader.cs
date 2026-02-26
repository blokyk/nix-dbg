internal class NoAnsiStream(Stream baseStream) : Stream
{
    public Stream BaseStream { get; } = baseStream;

    public override bool CanRead => BaseStream.CanRead;
    public override bool CanSeek => false; // todo: we *could* seek backwards, but not really forwards
    public override bool CanWrite => false;

    public override long Length { get; }
    public override long Position { get; set; }

    public override void Flush() => BaseStream.Flush();

    // fixme: does this need to be thread safe?
    /// <summary>
    /// Whether or not the last Read/Seek ended with the start
    /// of an escape sequence that wasn't closed (e.g. <c>\e[3</c>
    /// is unclosed, so we need to start the next read by making
    /// stripping the end of it (e.g. <c>1m</c>)).
    /// </summary>
    private bool hadUnclosedSequence = false;

    /// <summary>
    /// Number of bytes skipped since we started reading. Used for seeking.
    /// </summary>
    private int skippedBytes = 0;

    public override int Read(byte[] dest, int offset, int count) {
        var read = BaseStream.Read(dest, offset, count);

        var buffer = dest.AsSpan(offset, read);

        // to "erase" the escape sequences, we're going to be moving
        // parts of the buffer that do not contain the sequences over
        // the sequences, overwriting them.
        //
        // although in theory going front-to-back would be slightly
        // slower than back-to-front (due to the fact we'd be copying
        // stuff we'd later erase), it can actually be faster to do
        // it the natural way, because it's friendlier to the cache
        // and the CPU's pre-fetcher. Since we don't have a very high
        // ratio of escape sequences to normal characters, we wouldn't
        // be doing that much wasted work, so the gains from the cache
        // end up winning.

        var lastEscapeStartIdx = 0;
        var lastEscapeEndIdx = 0;
        var nextEscapeStartIdx = buffer.IndexOf((byte)'\e');

        if (nextEscapeStartIdx < 0)
            return read;

        // cases:
        //  start:
        //      1. stream starts with \e, unclosed
        //          -> copy nothing
        //      2. stream starts with \e, closed in the middle
        //          -> set copy start 1 after terminator
        //      3. stream starts with \e, stops at end
        //          -> don't copy anything
        //
        //  middle:
        //      4. \e in middle, unclosed
        //          -> copy from [copy_start..before_\e] to [last_seq_start..], set `unfinishedSequence` to true
        //      5. \e in middle, closed in middle
        //          -> set 

        // make sure that we didn't reach the end of the sequence yet,
        // and that there *is* a next escape sequence
        while (lastEscapeEndIdx < buffer.Length && nextEscapeStartIdx >= 0) {
            // todo: for the first loop, this is a useless copy
            buffer[lastEscapeEndIdx..nextEscapeStartIdx].CopyTo(buffer[lastEscapeStartIdx..]);

            // get the end of the current escape sequence
            lastEscapeEndIdx = buffer[nextEscapeStartIdx..].IndexOf((byte)'m') + 1;
            // and get the next escape sequence
            // (we do lastEscapeEndIdx just to be safe, in case)
            nextEscapeStartIdx = buffer[(lastEscapeEndIdx-1)..].IndexOf((byte)'\e');
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
    public override void SetLength(long value) => throw new NotImplementedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
}