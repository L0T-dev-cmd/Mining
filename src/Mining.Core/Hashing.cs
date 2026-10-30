using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Mining.Core;

/// <summary>Bitcoin consensus SHA256d and Stratum V1 header serialization.</summary>
public static class Hashing
{
    // Bitcoin compact target 0x1d00ffff: 0xffff * 256^(0x1d - 3).
    public static readonly BigInteger DifficultyOneTarget = new BigInteger(0xffff) << 208;
    public static readonly BigInteger MaximumTarget = (BigInteger.One << 256) - 1;

    public static byte[] DoubleSha256(ReadOnlySpan<byte> data)
    {
        var result = new byte[32];
        DoubleSha256(data, result);
        return result;
    }

    /// <summary>The hot path uses stack buffers and allocates no memory per nonce.</summary>
    public static void DoubleSha256(ReadOnlySpan<byte> data, Span<byte> result)
    {
        if (result.Length < 32) throw new ArgumentException("SHA256 requires a 32-byte output.");
        Span<byte> first = stackalloc byte[32];
        SHA256.HashData(data, first);
        SHA256.HashData(first, result);
    }

    /// <summary>Convert decimal difficulty exactly, without floating point rounding.</summary>
    public static BigInteger DifficultyTarget(decimal difficulty)
    {
        if (difficulty <= 0) throw new ArgumentOutOfRangeException(nameof(difficulty));
        int[] parts = decimal.GetBits(difficulty);
        BigInteger significand = (uint)parts[0] | ((BigInteger)(uint)parts[1] << 32) |
                                 ((BigInteger)(uint)parts[2] << 64);
        int scale = (parts[3] >> 16) & 0xff;
        BigInteger target = DifficultyOneTarget * BigInteger.Pow(10, scale) / significand;
        return BigInteger.Min(MaximumTarget, target);
    }

    public static bool MeetsTarget(ReadOnlySpan<byte> hash, BigInteger target)
    {
        if (hash.Length != 32) throw new ArgumentException("A Bitcoin hash has 32 bytes.");
        if (target < 0 || target > MaximumTarget) throw new ArgumentOutOfRangeException(nameof(target));
        // Bitcoin treats the raw SHA256 digest as a little-endian integer. The
        // displayed block hash is the reverse of these bytes.
        return new BigInteger(hash, isUnsigned: true, isBigEndian: false) <= target;
    }

    public static byte[] TargetBytes(BigInteger target)
    {
        if (target < 0 || target > MaximumTarget) throw new ArgumentOutOfRangeException(nameof(target));
        byte[] bytes = new byte[32];
        if (!target.TryWriteBytes(bytes, out _, isUnsigned: true, isBigEndian: false))
            throw new ArgumentException("Target exceeds 256 bits.");
        return bytes;
    }

    /// <summary>Allocation-free comparison for the mining loop; both inputs little endian.</summary>
    public static bool MeetsTarget(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> target)
    {
        if (hash.Length != 32 || target.Length != 32)
            throw new ArgumentException("Hash and target must have 32 bytes.");
        for (int i = 31; i >= 0; --i)
        {
            if (hash[i] < target[i]) return true;
            if (hash[i] > target[i]) return false;
        }
        return true;
    }

    public static string NonceHex(uint nonce) => nonce.ToString("x8", CultureInfo.InvariantCulture);

    public static byte[] BuildHeader(MiningJob job, string extraNonce2, uint nonce = 0)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrEmpty(job.Id) || job.Id.Length > 256)
            throw new ArgumentException("Invalid job identifier.");
        if (job.ExtraNonce2Size is < 1 or > 16)
            throw new ArgumentException("Unsupported extranonce2 size (expected 1–16 bytes).");
        byte[] en2 = Hex(extraNonce2, job.ExtraNonce2Size, nameof(extraNonce2));
        byte[] en1 = Hex(job.ExtraNonce1, null, nameof(job.ExtraNonce1), 128);
        byte[] prefix = Hex(job.CoinbasePrefix, null, nameof(job.CoinbasePrefix), 524288);
        byte[] suffix = Hex(job.CoinbaseSuffix, null, nameof(job.CoinbaseSuffix), 524288);
        if (job.MerkleBranches is null || job.MerkleBranches.Count > 32)
            throw new ArgumentException("Invalid Merkle branch count.");
        byte[] coinbase = new byte[checked(prefix.Length + en1.Length + en2.Length + suffix.Length)];
        int offset = 0;
        foreach (byte[] part in new[] { prefix, en1, en2, suffix })
        {
            part.CopyTo(coinbase, offset);
            offset += part.Length;
        }
        byte[] merkleRoot = DoubleSha256(coinbase);
        Span<byte> pair = stackalloc byte[64];
        foreach (string branch in job.MerkleBranches)
        {
            merkleRoot.CopyTo(pair);
            Hex(branch, 32, "Merkle branch").CopyTo(pair[32..]);
            DoubleSha256(pair, merkleRoot);
        }

        byte[] header = new byte[80];
        WriteNumericField(header.AsSpan(0, 4), job.Version, "version");
        byte[] previous = Hex(job.PreviousHash, 32, nameof(job.PreviousHash));
        // Stratum V1 prevhash uses getwork's 32-bit word encoding. Reverse
        // bytes within every word, not all 32 bytes as for a display hash.
        for (int i = 0; i < 32; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 + i, 4),
                BinaryPrimitives.ReadUInt32BigEndian(previous.AsSpan(i, 4)));
        // Merkle branches and SHA256 outputs are already raw digest bytes.
        merkleRoot.CopyTo(header, 36);
        WriteNumericField(header.AsSpan(68, 4), job.NTime, "ntime");
        WriteNumericField(header.AsSpan(72, 4), job.Bits, "nbits");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), nonce);
        return header;
    }

    private static void WriteNumericField(Span<byte> output, string value, string name)
    {
        byte[] bytes = Hex(value, 4, name);
        BinaryPrimitives.WriteUInt32LittleEndian(output, BinaryPrimitives.ReadUInt32BigEndian(bytes));
    }

    private static byte[] Hex(string? value, int? expectedBytes, string name, int maxCharacters = 64)
    {
        if (value is null || value.Length > maxCharacters || value.Length % 2 != 0 ||
            (expectedBytes.HasValue && value.Length != expectedBytes.Value * 2))
            throw new ArgumentException($"Invalid hexadecimal {name} length.");
        try { return Convert.FromHexString(value); }
        catch (FormatException e) { throw new ArgumentException($"Invalid hexadecimal {name}.", e); }
    }
}
