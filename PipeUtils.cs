using System.Buffers;
using System.IO.Pipelines;

namespace Blokyk.NixDebugAdapter;

internal static class PipeReaderUtils
{
    extension(PipeReader pipe) {
        /// <summary>
        /// Tries to read a whole line (including closing newline) from a <see cref="PipeReader"/> .
        /// </summary>
        /// <returns>
        /// A <see cref="ReadResult"/> containing the line buffer as well as information about
        /// the state of the last read
        /// </returns>
        internal async Task<ReadResult> ReadLineAsyncDebug(Action<string> log, CancellationToken ct = default) {
            ReadResult read;
            do {
                read = await pipe.ReadAtLeastAsync(1, ct);
                var buf = read.Buffer;

                var span = new byte[buf.Length];
                buf.CopyTo(span);
                log($"ReadLineAsync: {Convert.ToHexString(span)}");

                var lineEnd = buf.PositionOf((byte)'\n');
                if (lineEnd is not null) {
                    // we found the end of the line!
                    // get everything up-to and including the linebreak
                    var lineBuf = buf.Slice(buf.Start, buf.GetPosition(1, lineEnd.Value));
                    return new(lineBuf, read.IsCanceled, read.IsCompleted);
                } else {
                    // if this buffer doesn't have a newline, we have to advance the
                    // pipe and start again without changing state
                    pipe.AdvanceTo(consumed: buf.Start, examined: buf.End);
                    continue;
                }
            } while (!read.IsCompleted);

            // if we get here, it means the PipeReader stopped before we got a newline :(
            // thankfully, thanks to the PipeReader/ReadOnlySequence API, all the things
            // we previously read are still buffered, so we just need to ask the reader
            // one final time for the whole buffer
            var finalBuf = (await pipe.ReadAsync(ct)).Buffer;
            return new(finalBuf, read.IsCanceled, read.IsCompleted);
        }

        /// <summary>
        /// Tries to read a whole line (including closing newline) from a <see cref="PipeReader"/> .
        /// </summary>
        /// <returns>
        /// A <see cref="ReadResult"/> containing the line buffer as well as information about
        /// the state of the last read
        /// </returns>
        internal async Task<ReadResult> ReadLineAsync(CancellationToken ct = default) {
            ReadResult read;
            do {
                read = await pipe.ReadAtLeastAsync(1, ct);
                var buf = read.Buffer;

                var lineEnd = buf.PositionOf((byte)'\n');
                if (lineEnd is not null) {
                    // we found the end of the line!
                    // get everything up-to and including the linebreak
                    var lineBuf = buf.Slice(buf.Start, buf.GetPosition(1, lineEnd.Value));
                    return new(lineBuf, read.IsCanceled, read.IsCompleted);
                } else {
                    // if this buffer doesn't have a newline, we have to advance the
                    // pipe and start again without changing state
                    pipe.AdvanceTo(consumed: buf.Start, examined: buf.End);
                    continue;
                }
            } while (!read.IsCompleted);

            // if we get here, it means the PipeReader stopped before we got a newline :(
            // thankfully, thanks to the PipeReader/ReadOnlySequence API, all the things
            // we previously read are still buffered, so we just need to ask the reader
            // one final time for the whole buffer
            var finalBuf = (await pipe.ReadAsync(ct)).Buffer;
            return new(finalBuf, read.IsCanceled, read.IsCompleted);
        }

        internal async Task EatLine(CancellationToken ct) {
            var res = await pipe.ReadLineAsync(ct);
            pipe.AdvanceTo(res.Buffer.End);
        }

        internal async Task<bool> StartsWith(ReadOnlyMemory<byte> prefix, CancellationToken ct = default) {
            if (prefix.IsEmpty)
                return true;

            ReadResult read;
            SequenceReader<byte> seqReader;
            int matched = 0;
            do {
                read = await pipe.ReadAsync(ct);
                var buf = read.Buffer;

                seqReader = new SequenceReader<byte>(buf);
                while (matched < prefix.Length && seqReader.TryRead(out var currChar)) {
                    if (currChar != prefix.Span[matched]) {
                        pipe.AdvanceTo(buf.Start, seqReader.Position);
                        return false;
                    }

                    matched++;
                }

                if (matched == prefix.Length) {
                    pipe.AdvanceTo(buf.Start, seqReader.Position);
                    return true;
                }
            } while (matched < prefix.Length && !read.IsCompleted);

            pipe.AdvanceTo(seqReader.Sequence.Start, seqReader.Position);
            return matched == prefix.Length;
        }

        /// <summary>
        ///     Attempts to synchronously tell whether the <see cref="PipeReader"/> is completed.
        /// </summary>
        /// <returns>
        ///     <see langword="true"/> if the pipe reader is definitely completed;
        ///     <see langword="false"/> if it's not completed or it couldn't be determined synchronously.
        /// </returns>
        internal bool TryIsCompleted()
            => pipe.TryRead(out var res) && res.IsCompleted;
    }
}