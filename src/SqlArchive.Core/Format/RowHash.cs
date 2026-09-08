using System.Security.Cryptography;

namespace SqlArchive.Core.Format;

/// <summary>
/// The content hash of a table: one SHA-256 per canonical JSONL line, combined so that
/// the order the lines were read in does not change the answer.
/// <para>
/// Order independence is not a nicety. Export reads a large table in parallel ranges and
/// <c>verify</c> may read it back in a different order or with different range
/// boundaries; a hash that depended on the order would force both sides to sort, which
/// on a table of any size is the dominant cost. Independence from SQL Server matters for
/// the same kind of reason: the same row exported from 2016 and from 2022, through
/// connections with different collations, produces the same bytes because the encoding
/// is ours. <c>CHECKSUM_AGG</c> would have tied the answer to the engine's version.
/// </para>
/// <para><b>The combining operation is addition modulo 2^256, not XOR.</b> DESIGN.md
/// says XOR and asks, in the work package, whether the degenerate case matters: the XOR
/// of two identical rows is zero, so a table whose rows are each duplicated an even
/// number of times hashes the same as an empty one. The row count sitting next to the
/// hash in the manifest catches <i>that</i> case, but not the one it is a symptom of.
/// XOR is an involution, so any pair of multisets whose difference cancels in pairs
/// collides: <c>{A,A,B,B}</c> and <c>{C,C,D,D}</c> both hash to zero and both have four
/// rows, and a restore that copied one range twice and dropped another is exactly how
/// you get there. Addition keeps every property DESIGN.md asked for - it is commutative
/// and associative, so order and partitioning still do not matter, and it is a group, so
/// a contribution can still be removed - and duplicates no longer cancel. It is also the
/// standard construction for hashing a multiset rather than a set.</para>
/// </summary>
public static class RowHash
{
    /// <summary>The width of one row hash, and of the accumulator, in bytes.</summary>
    public const int Size = 32;

    /// <summary>
    /// The SHA-256 of one canonical JSONL line.
    /// </summary>
    /// <param name="canonicalJsonLine">
    /// The line's bytes <b>without</b> its terminator. The newline is a property of the
    /// file, not of the row: a row written as the last line of a range file and the same
    /// row written in the middle of a whole-table file have to hash the same, or reading
    /// a table back with different range boundaries would report drift that is not there.
    /// </param>
    public static byte[] OfRow(ReadOnlySpan<byte> canonicalJsonLine)
    {
        var hash = new byte[Size];
        SHA256.HashData(canonicalJsonLine, hash);
        return hash;
    }

    /// <summary>Writes the SHA-256 of a line into a caller-owned buffer, allocating nothing.</summary>
    public static void OfRow(ReadOnlySpan<byte> canonicalJsonLine, Span<byte> destination)
    {
        if(destination.Length < Size)
            throw new ArgumentException($"A row hash needs {Size} bytes.", nameof(destination));

        SHA256.HashData(canonicalJsonLine, destination);
    }

    /// <summary>
    /// Accumulates row hashes into a table hash without depending on the order they
    /// arrive in.
    /// <para>
    /// One instance is not thread-safe. A parallel export gives each range its own and
    /// folds them together with <see cref="Add(Accumulator)"/> at the end, which is both
    /// faster than locking and the same answer.
    /// </para>
    /// </summary>
    public sealed class Accumulator
    {
        // Big-endian: index 0 is the most significant byte, so the hex form of the
        // buffer reads as the number it is. Carry therefore propagates from the end
        // towards the front.
        private readonly byte[] _total = new byte[Size];

        /// <summary>How many rows have been added.</summary>
        public long Rows { get; private set; }

        /// <summary>
        /// The table hash so far, as lowercase hex. An empty table is 64 zeros; the row
        /// count in the manifest is what distinguishes that from the vanishingly
        /// unlikely non-empty table that also sums to zero.
        /// </summary>
        public string Value => Convert.ToHexStringLower(_total);

        /// <summary>The raw 32 bytes, for a caller that wants to compare without formatting.</summary>
        public ReadOnlySpan<byte> Bytes => _total;

        public void Add(ReadOnlySpan<byte> rowHash)
        {
            if(rowHash.Length != Size)
                throw new ArgumentException($"A row hash is {Size} bytes, not {rowHash.Length}.", nameof(rowHash));

            var carry = 0;

            for(var i = Size - 1; i >= 0; i--)
            {
                var sum = _total[i] + rowHash[i] + carry;
                _total[i] = (byte)sum;
                carry = sum >> 8;
            }

            // The carry out of the most significant byte is dropped, which is what
            // "modulo 2^256" means.
            Rows++;
        }

        /// <summary>
        /// Folds another accumulator in, rows and all. Used to combine the ranges of one
        /// table that were read in parallel.
        /// </summary>
        public void Add(Accumulator other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var carry = 0;

            for(var i = Size - 1; i >= 0; i--)
            {
                var sum = _total[i] + other._total[i] + carry;
                _total[i] = (byte)sum;
                carry = sum >> 8;
            }

            Rows += other.Rows;
        }

        /// <summary>Hashes a line and adds it, which is what a writer does for every row.</summary>
        public void AddRow(ReadOnlySpan<byte> canonicalJsonLine)
        {
            Span<byte> hash = stackalloc byte[Size];
            OfRow(canonicalJsonLine, hash);
            Add(hash);
        }
    }
}
